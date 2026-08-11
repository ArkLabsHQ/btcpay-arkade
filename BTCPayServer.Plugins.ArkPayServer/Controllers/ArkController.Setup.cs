using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Wallets;
using NArk.Core.Wallet;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkController
{
    [HttpGet("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult InitialSetup(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId == null)
        {
            return View(new InitialWalletSetupViewModel());
        }

        return RedirectToAction("StoreOverview", new { storeId });
    }

    [HttpPost("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, InitialWalletSetupViewModel model)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        try
        {
            var walletSettings = await GetFromInputWallet(model.Wallet, model.Mode);

            if (walletSettings.Wallet is not null)
            {
                try
                {
                    var serverInfo = await clientTransport.GetServerInfoAsync(HttpContext.RequestAborted);

                    // Watch-only import: walletSettings.Wallet carries the
                    // account descriptor verbatim. Hand it to NArk's
                    // CreateWatchOnlyWallet helper (added in dotnet-sdk#107)
                    // which leaves Secret null on the resulting ArkWalletInfo
                    // so DefaultWalletProvider.GetSignerAsync returns null
                    // unless an IRemoteSignerTransport claims the wallet.
                    // The factory throws on an unparseable descriptor and the
                    // outer try/catch surfaces that to the form below.
                    var wallet = walletSettings.IsWatchOnlyDescriptor
                        ? await WalletFactory.CreateWatchOnlyWallet(
                            walletSettings.Wallet,
                            destination: walletSettings.Destination,
                            serverInfo,
                            metadata: null,
                            HttpContext.RequestAborted)
                        : await WalletFactory.CreateWallet(
                            walletSettings.Wallet,
                            walletSettings.Destination,
                            serverInfo,
                            HttpContext.RequestAborted);

                    // Signer is automatically registered via WalletSaved event
                    await walletStorage.UpsertWallet(wallet, updateIfExists: true, HttpContext.RequestAborted);

                    if (wallet.WalletType == WalletType.SingleKey)
                    {
                        // Synchronous default-contract creation at setup (no regression vs. the
                        // prior inline DeriveContract), but the "how" now lives in the SDK. The
                        // persisted Default is thereafter maintained across signer rotation by
                        // the SDK's ContractReconciliationService (started via ArkHostedLifecycle).
                        await singleKeyDefaultEnsurer.EnsureDefaultAsync(wallet.Id, HttpContext.RequestAborted);
                    }

                    walletSettings = walletSettings with { WalletId = wallet.Id };
                }
                catch (Exception ex)
                {
                    TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Could not update wallet");
                    return View(model);
                }
            }

            // On import, recover the wallet in the background (off the request thread —
            // a gap-limit scan polls arkd per index): discover contracts across derivation
            // indices + server signers (incl. legacy/deprecated), restore swaps, finalize
            // pending txs, resync funds, then sync boarding UTXOs.
            StartBackgroundRecovery(walletSettings.WalletId!);

            var config = new ArkadePaymentMethodConfig(walletSettings.WalletId!, walletSettings.IsOwnedByStore);
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);

            // Set Arkade as the default payment method
            store.SetDefaultPaymentId(ArkadePlugin.ArkadePaymentMethodId);

            // Enable Lightning by default if not already configured. Skip watch-only wallets:
            // Arkade-backed Lightning needs batch participation (signing), and without a paired
            // remote signer the wallet would accept LN invoices at checkout but fail at
            // settlement after the customer has already committed to paying. The merchant can
            // still flip it on manually once a companion signer is paired.
            var lightningPaymentMethodId = GetLightningPaymentMethod();
            var existingLnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lightningPaymentMethodId, paymentMethodHandlerDictionary);
            if (existingLnConfig == null && !walletSettings.IsWatchOnlyDescriptor)
            {
                var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
                
                var lnConfig = new LightningPaymentMethodConfig()
                {
                    ConnectionString = config.GeneratedByStore
                        ? await spendKeyService.BuildConnectionStringAsync(config.WalletId)
                        : ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(config.WalletId),
                };

                store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
                store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lnurlPaymentMethodId], new LNURLPaymentMethodConfig
                {
                    UseBech32Scheme = true,
                    LUD12Enabled = false
                });
                
                var blob = store.GetStoreBlob();
                blob.SetExcluded(lightningPaymentMethodId, false);
                blob.OnChainWithLnInvoiceFallback = true;
                store.SetStoreBlob(blob);
            }

            await storeRepository.UpdateStore(store);

            // If a new HD wallet was generated, redirect to seed backup page
            if (walletSettings is { IsNewlyGeneratedWallet: true, Wallet: not null })
            {
                return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
                {
                    ReturnUrl = Url.Action(nameof(StoreOverview), new { storeId }),
                    IsStored = true,
                    RequireConfirm = true,
                    CryptoCode = "ARK",
                    Mnemonic = walletSettings.Wallet
                });
            }

            TempData[WellKnownTempData.SuccessMessage] = "Arkade payment method updated.";

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(nameof(model.Wallet), ex.Message);
            return View(model);
        }
    }
}