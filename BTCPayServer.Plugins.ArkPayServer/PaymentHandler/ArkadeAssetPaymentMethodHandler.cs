using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// The dedicated Arkade Asset payment method. Unlike the BTC-VTXO ARKADE method,
/// this prices the invoice in every enabled tracked asset and exposes one option
/// per asset (each a BIP-321 URI with the same Ark address + that asset's id +
/// amount due). The payer picks which asset to send; settlement is detected by
/// which asset actually arrives (see <c>ArkContractInvoiceListener</c>).
/// </summary>
public class ArkadeAssetPaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    IContractService contractService,
    IClientTransport clientTransport,
    PaymentMethodHandlerDictionary handlers,
    AssetRateResolver assetRateResolver
) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadeAssetPaymentMethodId;

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        try
        {
            _ = await clientTransport.GetServerInfoAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        var store = context.Store;

        // The asset method is enabled by its own thin config; the wallet and the
        // tracked-asset list live on the BTC-VTXO ARKADE config (single source of truth).
        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not ArkadeAssetPaymentMethodConfig)
            throw new PaymentMethodUnavailableException("Arkade Asset payment method not configured");

        var arkadeConfig = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
            ArkadePlugin.ArkadePaymentMethodId, handlers);
        if (arkadeConfig?.WalletId is not { } walletId)
            throw new PaymentMethodUnavailableException("Arkade wallet not configured");

        var enabledAssets = arkadeConfig.Assets.Where(a => a.Enabled).ToList();
        if (enabledAssets.Count == 0)
            throw new PaymentMethodUnavailableException("No Arkade asset is enabled for payment");

        // Derive a dedicated Ark receive address for this invoice (same path the
        // BTC method uses); all asset options settle to this one address.
        var contract = await contractService.DeriveContract(
            walletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);
        var address = contract.GetArkAddress();
        var arkAddress = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);

        var dueSats = (long)Money.Coins(context.Prompt.Calculate().Due).Satoshi;
        var options = new List<ArkadeAssetOption>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var asset in enabledAssets)
        {
            AssetAmountDue due;
            try
            {
                due = await assetRateResolver.ResolveAsync(store, asset, dueSats, cts.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                // An asset whose rate can't be evaluated simply isn't offered — never a hard failure.
                context.Logs.Write(
                    $"Arkade asset {asset.CurrencyCode} unavailable for this invoice: {ex.Message}",
                    InvoiceEventData.EventSeverity.Warning);
                continue;
            }

            var uri = ArkadeBip21Builder.Create()
                .WithArkAddress(arkAddress)
                .WithAsset(asset.AssetId, due.DisplayUnits)
                .Build();
            options.Add(new ArkadeAssetOption(
                asset.AssetId, asset.CurrencyCode, asset.Ticker, asset.Decimals,
                due.BaseUnits, due.FormattedAmount, uri));
        }

        if (options.Count == 0)
            throw new PaymentMethodUnavailableException("No Arkade asset is available for this invoice.");

        context.Prompt.Destination = arkAddress;
        context.Prompt.PaymentMethodFee = 0m;
        context.TrackedDestinations.Add(arkAddress);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());

        context.Prompt.Details = JObject.FromObject(new ArkadeAssetPromptDetails
        {
            WalletId = walletId,
            ArkAddress = arkAddress,
            ContractString = contract.ToString(),
            Options = options,
        }, Serializer);
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        return Task.CompletedTask;
    }

    public ArkadeAssetPromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadeAssetPromptDetails>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadeAssetPromptDetails)}");
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadeAssetPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadeAssetPaymentMethodHandler)}");
    }

    public ArkadePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentData)}");
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}
