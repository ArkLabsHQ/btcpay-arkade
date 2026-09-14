using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class ArkEvmSettlementController(IArkEvmSettlementStore settlementStore, IWalletProvider walletProvider,
    ArkEvmRpcEndpointProtector rpcProtector, ArkEvmGasPayerProtector gasPayerProtector,
    ArkCompositionPromptService? compositionPrompts = null, IArkCompositionExecutionLock? executionLock = null) : ControllerBase
{
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetConfiguration(string storeId)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        var settings = configuration?.EvmSettlement;
        return settings is null ? NoContent() : Ok(ToData(store, settings, !string.IsNullOrWhiteSpace(configuration?.WalletId)));
    }

    [HttpPut("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [ArkEvmSettlementValidation]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SetConfiguration(string storeId,
        [FromBody, ModelBinder(BinderType = typeof(ArkEvmSettlementUpdateBinder))] ArkEvmSettlementUpdateData update,
        CancellationToken cancellationToken)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        if (string.IsNullOrWhiteSpace(configuration?.WalletId))
            return Conflict(new { code = "arkade-not-configured", message = "Configure an Arkade wallet for this store first." });
        ArkEvmSettlementSettings settings;
        try
        {
            var rpc = update.RpcEndpoint;
            var protectedRpc = rpc switch
            {
                null => configuration.EvmSettlement?.ProtectedRpcUri,
                { Action: "preserve", Uri: null } => configuration.EvmSettlement?.ProtectedRpcUri,
                { Action: "clear", Uri: null } => null,
                { Action: "replace" } => rpcProtector.Protect(storeId, rpc.Uri),
                _ => throw new ArgumentException("Specify a valid RPC endpoint update.")
            };
            var gasPayer = update.GasPayerPrivateKey;
            var protectedGasPayer = gasPayer switch
            {
                null => configuration.EvmSettlement?.ProtectedGasPayerPrivateKey,
                { Action: "preserve", PrivateKey: null } => configuration.EvmSettlement?.ProtectedGasPayerPrivateKey,
                { Action: "clear", PrivateKey: null } => null,
                { Action: "replace" } => gasPayerProtector.Protect(storeId, gasPayer.PrivateKey,
                    update.ExpectedSenderAddress),
                _ => throw new ArgumentException("Specify a valid gas-payer key update.")
            };
            settings = new ArkEvmSettlementSettings(update.AssetId, update.Destination, update.Enabled)
            {
                RoutePolicy = update.RoutePolicy,
                ProtectedRpcUri = protectedRpc,
                ProtectedGasPayerPrivateKey = protectedGasPayer,
                ExpectedSenderAddress = update.ExpectedSenderAddress,
                MaxFeePerGasWei = update.MaxFeePerGasWei,
                MaxPriorityFeePerGasWei = update.MaxPriorityFeePerGasWei,
                MaxGasLimit = update.MaxGasLimit
            }.Validate();
            if (settings.Enabled && !ToData(store, settings).ConfigurationComplete)
                throw new ArgumentException("Enabled settlement requires a complete route policy and RPC endpoint.");
        }
        catch (ArgumentException)
        {
            return ArkEvmSettlementValidationAttribute.InvalidSettings();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await settlementStore.SaveAsync(store, configuration with { EvmSettlement = settings });
        return Ok(ToData(store, settings));
    }

    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/capabilities")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetCapabilities(string storeId, CancellationToken cancellationToken)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        var walletConfigured = !string.IsNullOrWhiteSpace(configuration?.WalletId);
        var signerAvailable = walletConfigured &&
                              await walletProvider.GetSignerAsync(configuration!.WalletId, cancellationToken) is not null;
        var settings = configuration?.EvmSettlement;
        var data = settings is null ? null : ToData(store, settings, walletConfigured);
        var lockAvailable = executionLock?.SupportsCrossProcessExecution == true;
        var missing = data?.MissingConfiguration ?? (walletConfigured ? ["settlement-settings-missing"] :
            ["arkade-wallet-missing", "arkade-payment-method-missing", "settlement-settings-missing"]);
        if (!lockAvailable) missing = [.. missing, "cross-process-execution-lock-unavailable"];
        return Ok(new ArkEvmSettlementCapabilitiesData(walletConfigured, signerAvailable, settings?.Enabled == true,
            data?.ConfigurationComplete == true, settings?.RoutePolicy?.EnabledSourceRails ?? [],
            data?.RpcEndpointConfigured == true, data?.RpcEndpointOrigin, data?.GasPayerConfigured == true,
            missing, compositionPrompts is not null, lockAvailable));
    }

    private ArkEvmSettlementData ToData(StoreData store, ArkEvmSettlementSettings settings, bool walletConfigured = true)
    {
        var rpc = rpcProtector.TryUnprotect(store.Id, settings.ProtectedRpcUri);
        var missing = new List<string>();
        if (!walletConfigured) missing.Add("arkade-wallet-missing");
        if (settings.RoutePolicy is null) missing.Add("route-policy-missing");
        if (rpc is null) missing.Add(settings.ProtectedRpcUri is null ? "rpc-endpoint-missing" : "rpc-endpoint-unavailable");
        if (!HasEnabledArkadePaymentMethod(store)) missing.Add("arkade-payment-method-missing");
        if (settings.RoutePolicy?.EnabledSourceRails.Contains("BTC-CHAIN") == true &&
            !store.GetPaymentMethodConfigs(true).ContainsKey(PaymentTypes.CHAIN.GetPaymentMethodId("BTC")))
            missing.Add("onchain-payment-method-missing");
        if (settings.RoutePolicy?.EnabledSourceRails.Contains("BTC-LN") == true &&
            !HasArkadeLightningPaymentMethod(store, ArkCompositionPromptService.Configuration(store)?.WalletId))
            missing.Add("lightning-payment-method-missing");
        var gasPayerConfigured = settings.ExpectedSenderAddress is not null && gasPayerProtector.IsAvailable(store.Id,
            settings.ProtectedGasPayerPrivateKey, settings.ExpectedSenderAddress);
        if (settings.ExpectedSenderAddress is null) missing.Add("gas-payer-address-missing");
        if (settings.ProtectedGasPayerPrivateKey is null) missing.Add("gas-payer-key-missing");
        else if (settings.ExpectedSenderAddress is not null && !gasPayerConfigured)
            missing.Add("gas-payer-key-unavailable");
        if (settings.MaxFeePerGasWei is null) missing.Add("max-fee-per-gas-missing");
        if (settings.MaxPriorityFeePerGasWei is null) missing.Add("max-priority-fee-per-gas-missing");
        if (settings.MaxGasLimit is null) missing.Add("max-gas-limit-missing");
        var origin = rpc is null ? null : new UriBuilder(rpc.Scheme, rpc.Host, rpc.IsDefaultPort ? -1 : rpc.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        return new ArkEvmSettlementData(settings.AssetId, settings.Destination, settings.Enabled, settings.RoutePolicy,
            rpc is not null, origin, settings.ExpectedSenderAddress, settings.MaxFeePerGasWei,
            settings.MaxPriorityFeePerGasWei, settings.MaxGasLimit,
            gasPayerConfigured, missing.ToArray());
    }

    private static bool HasEnabledArkadePaymentMethod(StoreData store) =>
        store.GetPaymentMethodConfigs(true).ContainsKey(ArkadePlugin.ArkadePaymentMethodId);

    private static bool HasArkadeLightningPaymentMethod(StoreData store, string? walletId)
    {
        if (string.IsNullOrWhiteSpace(walletId) ||
            !store.GetPaymentMethodConfigs(true).TryGetValue(PaymentTypes.LN.GetPaymentMethodId("BTC"), out var value))
            return false;
        try
        {
            var config = value.ToObject<LightningPaymentMethodConfig>(BlobSerializer.CreateSerializer().Serializer);
            if (string.IsNullOrWhiteSpace(config?.ConnectionString)) return false;
            var values = LightningConnectionStringHelper.ExtractValues(config.ConnectionString, out var type);
            return type == "arkade" && values.TryGetValue("wallet-id", out var configuredWallet) &&
                   configuredWallet == walletId &&
                   values.TryGetValue("store-id", out var configuredStore) && configuredStore == store.Id;
        }
        catch
        {
            return false;
        }
    }

    private StoreData? OwnedStore(string storeId) =>
        HttpContext.GetStoreDataOrNull() is { } store && string.Equals(store.Id, storeId, StringComparison.Ordinal)
            ? store : null;
}
