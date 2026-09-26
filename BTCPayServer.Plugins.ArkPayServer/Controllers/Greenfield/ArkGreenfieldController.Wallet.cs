using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
    /// <summary>
    /// Get Arkade wallet information for a store.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/wallet")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetWallet(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        var wallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(config.WalletId!, cancellationToken) != null;

        string? defaultAddress = null;
        if (wallet?.WalletType == WalletType.SingleKey)
        {
            try
            {
                var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
                var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, terms.Network);
                var defaultContract = new ArkPaymentContract(terms.SignerKey, terms.UnilateralExit, descriptor);
                defaultAddress = defaultContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);
            }
            catch
            {
                // Operator unavailable — skip default address
            }
        }

        return Ok(new ArkWalletData
        {
            WalletId = config.WalletId!,
            WalletType = (wallet?.WalletType ?? WalletType.SingleKey).ToString(),
            SignerAvailable = signerAvailable,
            IsOwnedByStore = config.GeneratedByStore,
            DefaultAddress = defaultAddress,
            Destination = wallet?.Destination,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            BoardingEnabled = config.BoardingEnabled,
            OnchainSwapEnabled = config.OnchainSwapEnabled,
            MinBoardingAmountSats = config.MinBoardingAmountSats,
            LightningEnabled = IsArkadeLightningEnabled()
        });
    }

    /// <summary>
    /// Create or import an Arkade wallet for a store.
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SetupWallet(string storeId, [FromBody] ArkWalletSetupRequest request,
        CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store == null) return NotFound();

        var existingConfig = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (existingConfig?.WalletId != null)
            return this.CreateAPIError(409, "wallet-already-configured",
                "Arkade wallet is already configured for this store. Delete it first to set up a new one.");

        try
        {
            var (walletInfo, walletId, isNew, mnemonic) = await ResolveWalletInput(
                request.Wallet, request.Destination, cancellationToken);

            if (walletInfo != null)
            {
                await walletStorage.UpsertWallet(walletInfo, updateIfExists: true, cancellationToken);

                if (walletInfo.WalletType == WalletType.SingleKey)
                {
                    // Delegate to the SDK's ensurer (destination-insensitive, unlike the
                    // SendToSelf path); the SDK ContractReconciliationService also maintains
                    // this default across signer rotation.
                    await singleKeyDefaultEnsurer.EnsureDefaultAsync(walletInfo.Id, cancellationToken);
                }

                walletId = walletInfo.Id;
            }

            // Sync existing contracts if linking an existing wallet: boarding (on-chain) drives the
            // UTXO sync, off-chain (VTXO) scripts drive the indexer poll.
            var boardingContracts = await contractStorage.GetContracts(
                walletIds: [walletId!], scope: ContractScope.Onchain, cancellationToken: cancellationToken);
            var offchainScripts = (await contractStorage.GetContracts(
                    walletIds: [walletId!], scope: ContractScope.Offchain, cancellationToken: cancellationToken))
                .Select(c => c.Script).ToHashSet();
            if (offchainScripts.Count > 0)
                await vtxoSyncService.PollScriptsForVtxos(offchainScripts, cancellationToken);
            if (boardingContracts.Count > 0)
                await boardingUtxoSyncService.SyncAsync(boardingContracts, cancellationToken);

            var config = new ArkadePaymentMethodConfig(walletId!, isNew);
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);
            store.SetDefaultPaymentId(ArkadePlugin.ArkadePaymentMethodId);

            // Enable Lightning if requested
            var lightningEnabled = false;
            if (request.EnableLightning)
            {
                lightningEnabled = await ConfigureLightning(store, walletId!, isNew, cancellationToken);
            }

            await storeRepository.UpdateStore(store);

            return Ok(new ArkWalletSetupResponse
            {
                WalletId = walletId!,
                WalletType = (walletInfo?.WalletType ?? WalletType.SingleKey).ToString(),
                IsNewWallet = isNew,
                LightningEnabled = lightningEnabled,
                Mnemonic = mnemonic
            });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("wallet-setup-failed", ex.Message);
        }
    }

    /// <summary>
    /// Update Arkade wallet settings for a store.
    /// </summary>
    [HttpPatch("~/api/v1/stores/{storeId}/arkade/wallet/settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> UpdateWalletSettings(string storeId,
        [FromBody] ArkWalletSettingsRequest request, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store == null) return NotFound();

        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        var newConfig = config!;

        // Handle destination update
        if (request.Destination != null)
        {
            if (request.Destination == "")
            {
                // Clear destination
                await walletStorage.UpdateDestination(config.WalletId, null, cancellationToken);
            }
            else
            {
                if (config.AllowSubDustAmounts)
                    return this.CreateAPIError("invalid-settings",
                        "Cannot set auto-sweep destination while sub-dust amounts are enabled.");

                var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
                WalletFactory.ValidateDestination(request.Destination, serverInfo);
                await walletStorage.UpdateDestination(config.WalletId, request.Destination, cancellationToken);
            }
        }

        // Handle sub-dust toggle
        if (request.AllowSubDustAmounts is { } allowSubDust)
        {
            if (allowSubDust)
            {
                var wallet = await walletStorage.GetWalletById(config.WalletId, cancellationToken);
                if (!string.IsNullOrEmpty(wallet?.Destination))
                    return this.CreateAPIError("invalid-settings",
                        "Cannot enable sub-dust amounts while auto-sweep destination is configured.");
            }
            newConfig = newConfig with { AllowSubDustAmounts = allowSubDust };
        }

        // Handle boarding settings
        if (request.OnchainSwapEnabled is { } swapEnabled)
        {
            // The swap's refund lands on the boarding address, so the two cannot be set
            // independently: enabling one requires the other, and disabling boarding takes the
            // swap with it. Applied before BoardingEnabled so an explicit `false` there still wins.
            newConfig = newConfig with
            {
                OnchainSwapEnabled = swapEnabled,
                BoardingEnabled = swapEnabled || newConfig.BoardingEnabled,
            };
        }

        if (request.BoardingEnabled is { } boardingEnabled)
        {
            newConfig = newConfig with
            {
                BoardingEnabled = boardingEnabled,
                OnchainSwapEnabled = boardingEnabled && newConfig.OnchainSwapEnabled,
            };
        }

        if (request.MinBoardingAmountSats is { } minAmount)
        {
            if (minAmount < 330)
                return this.CreateAPIError("invalid-settings",
                    "Boarding minimum cannot be below the P2TR dust threshold (330 sats).");
            newConfig = newConfig with { MinBoardingAmountSats = minAmount };
        }

        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
        await storeRepository.UpdateStore(store);

        // Return updated wallet info
        return await GetWallet(storeId, cancellationToken);
    }

    /// <summary>
    /// Remove the Arkade wallet configuration from a store.
    /// This does NOT delete the underlying wallet data — it only unlinks it from the store.
    /// </summary>
    [HttpDelete("~/api/v1/stores/{storeId}/arkade/wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> RemoveWallet(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store == null) return NotFound();

        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        // Remove Arkade payment method config
        store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);

        // Remove Arkade Lightning if it was configured
        var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lnPaymentMethodId, paymentMethodHandlerDictionary);
        if (lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true)
        {
            store.SetPaymentMethodConfig(lnPaymentMethodId, null);

            var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
            store.SetPaymentMethodConfig(lnurlPaymentMethodId, null);
        }

        await storeRepository.UpdateStore(store);

        return Ok(new { removed = true, walletId = config!.WalletId });
    }
}
