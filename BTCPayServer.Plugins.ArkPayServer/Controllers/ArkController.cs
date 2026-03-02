using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Core.Contracts;
using NArk.Hosting;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;
using NArk.Storage.EfCore.Entities;
using NArk.Core.Wallet;
using LNURL;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using ArkIntent = NArk.Abstractions.Intents.ArkIntent;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController(
    BoltzLimitsValidator? boltzLimitsValidator,
    BoltzClient? boltzClient,
    ArkNetworkConfig arkNetworkConfig,
    IAuthorizationService authorizationService,
    ArkPayoutHandler arkPayoutHandler,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IClientTransport clientTransport,
    ArkadeSpendingService arkadeSpendingService,
    ArkAutomatedPayoutSenderFactory payoutSenderFactory,
    PayoutProcessorService payoutProcessorService,
    PullPaymentHostedService pullPaymentHostedService,
    EventAggregator eventAggregator,
    IIntentGenerationService intentGenerationService,
    IIntentStorage intentStorage,
    IWalletProvider walletProvider,
    ISpendingService arkadeSpender,
    IFeeEstimator feeEstimator,
    IContractService contractService,
    IChainTimeProvider bitcoinTimeChainProvider,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    IWalletStorage walletStorage,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory) : Controller
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
            var walletSettings = await GetFromInputWallet(model.Wallet);

            if (walletSettings.Wallet is not null)
            {
                try
                {
                    var serverInfo = await clientTransport.GetServerInfoAsync(HttpContext.RequestAborted);
                    var wallet = await WalletFactory.CreateWallet(
                        walletSettings.Wallet,
                        walletSettings.Destination,
                        serverInfo,
                        HttpContext.RequestAborted);

                    // Signer is automatically registered via WalletSaved event
                    await walletStorage.UpsertWallet(wallet, updateIfExists: true, HttpContext.RequestAborted);
                    
                    if (wallet.WalletType == WalletType.SingleKey)
                    {
                       await  contractService.DeriveContract(
                           wallet.Id, 
                           NextContractPurpose.SendToSelf, 
                           ContractActivityState.Active, 
                           metadata: new Dictionary<string, string> { ["Source"] = "Default" },
                           cancellationToken: HttpContext.RequestAborted);
                    }
                    
                    walletSettings = walletSettings with { WalletId = wallet.Id };
                }
                catch (Exception ex)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Could not update wallet: " + ex.Message;
                    return View(model);
                }
            }

            var config = new ArkadePaymentMethodConfig(walletSettings.WalletId!, walletSettings.IsOwnedByStore);
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);

            // Set Arkade as the default payment method
            store.SetDefaultPaymentId(ArkadePlugin.ArkadePaymentMethodId);

            // Enable Lightning by default if not already configured
            var lightningPaymentMethodId = GetLightningPaymentMethod();
            var existingLnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lightningPaymentMethodId, paymentMethodHandlerDictionary);
            if (existingLnConfig == null)
            {
                var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
                
                var lnConfig = new LightningPaymentMethodConfig()
                {
                    ConnectionString = $"type=arkade;wallet-id={config.WalletId}",
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

            TempData[WellKnownTempData.SuccessMessage] = "Ark Payment method updated.";

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(nameof(model.Wallet), ex.Message);
            return View(model);
        }
    }

    [HttpGet("stores/{storeId}/overview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> StoreOverview(CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var wallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);
        var destination = wallet?.Destination;

        // Get balances with error handling - indexer service may be unavailable
        ArkBalancesViewModel? balances = null;
        try
        {
            balances = await GetArkBalances(config.WalletId!, cancellationToken);
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Unable to fetch balances: {ex.Message}";
        }

        var signerAvailable = await walletProvider.GetAddressProviderAsync(config.WalletId!, cancellationToken) != null;

        // Get the default/active contract address
        string? defaultAddress = null;
        if (wallet?.WalletType == WalletType.SingleKey)
        {
            // SingleKey: compute the deterministic default address directly from the wallet key
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
            var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, terms.Network);
            var defaultContract = new ArkPaymentContract(terms.SignerKey, terms.UnilateralExit, descriptor);
            defaultAddress = defaultContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);
        }

        // Check Ark Operator connection
        var (arkOperatorConnected, arkOperatorError) = await CheckServiceConnectionAsync(
            ct => clientTransport.GetServerInfoAsync(ct), cancellationToken);

        // Check Boltz connection and get cached limits
        var (boltzConnected, boltzError, boltzLimits) = await GetBoltzConnectionStatusAsync(cancellationToken);

        // Determine if user can manage private keys (spend/view keys)
        // Allowed if: wallet was generated by this store OR user is server admin
        var canManagePrivateKeys = config!.GeneratedByStore ||
            (await authorizationService.AuthorizeAsync(User, null, new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;

        // Get recent VTXOs for the overview (latest 5, including spent)
        IReadOnlyCollection<ArkVtxo> recentVtxos = [];
        HashSet<OutPoint> spendableOutpoints = [];
        Dictionary<string, ArkContractEntity> vtxoContracts = new();
        var totalVtxoCount = 0;
        try
        {
            var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, cancellationToken);
            spendableOutpoints = allCoins.Select(c => c.Outpoint).ToHashSet();

            var vtxos = await vtxoStorage.GetVtxos(
                walletIds: [config.WalletId!],
                includeSpent: true,
                take: 5,
                cancellationToken: cancellationToken);
            recentVtxos = vtxos.ToList();

            // Get total count (all VTXOs including spent)
            var allVtxos = await vtxoStorage.GetVtxos(
                walletIds: [config.WalletId!],
                includeSpent: true,
                cancellationToken: cancellationToken);
            totalVtxoCount = allVtxos.Count();

            // Get contract info for VTXOs
            var vtxoScripts = recentVtxos.Select(v => v.Script).Distinct().ToArray();
            var contracts = await contractStorage.GetContracts(
                walletIds: [config.WalletId!],
                scripts: vtxoScripts,
                cancellationToken: cancellationToken);
            vtxoContracts = contracts.ToDictionary(c => c.Script);
        }
        catch (Exception)
        {
            // Silently ignore - VTXOs section will show empty
        }

        // Get recent intents (latest 5)
        IReadOnlyCollection<ArkIntent> recentIntents = [];
        try
        {
            recentIntents = await intentStorage.GetIntents(
                walletIds:[config.WalletId!], skip: 0, take: 5, states: [ArkIntentState.BatchInProgress, ArkIntentState.BatchSucceeded, ArkIntentState.WaitingForBatch, ArkIntentState.WaitingToSubmit], cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // Silently ignore - intents section will show empty
        }

        // Get recent swaps (latest 5)
        IReadOnlyCollection<NArk.Swaps.Models.ArkSwap> recentSwaps = [];
        try
        {
            recentSwaps = await swapStorage.GetSwaps(
                walletIds: [config.WalletId!], take: 5, status: [ArkSwapStatus.Pending , ArkSwapStatus.Settled], cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // Silently ignore - swaps section will show empty
        }

        return View(new StoreOverviewViewModel
        {
            StoreId = store!.Id,
            IsDestinationSweepEnabled = destination is not null,
            IsLightningEnabled = IsArkadeLightningEnabled(),
            Balances = balances,
            WalletId = config.WalletId,
            Destination = destination,
            SignerAvailable = signerAvailable,
            DefaultAddress = defaultAddress,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            Wallet = wallet?.Secret,
            WalletType = wallet?.WalletType ?? WalletType.SingleKey,
            CanManagePrivateKeys = canManagePrivateKeys,
            ArkOperatorUrl = arkNetworkConfig.ArkUri,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = arkOperatorError,
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError,
            BoltzReverseMinAmount = boltzLimits?.ReverseMinAmount,
            BoltzReverseMaxAmount = boltzLimits?.ReverseMaxAmount,
            BoltzReverseFeePercentage = boltzLimits?.ReverseFeePercentage,
            BoltzReverseMinerFee = boltzLimits?.ReverseMinerFee,
            BoltzSubmarineMinAmount = boltzLimits?.SubmarineMinAmount,
            BoltzSubmarineMaxAmount = boltzLimits?.SubmarineMaxAmount,
            BoltzSubmarineFeePercentage = boltzLimits?.SubmarineFeePercentage,
            BoltzSubmarineMinerFee = boltzLimits?.SubmarineMinerFee,
            RecentVtxos = recentVtxos,
            SpendableOutpoints = spendableOutpoints,
            VtxoContracts = vtxoContracts,
            TotalVtxoCount = totalVtxoCount,
            RecentIntents = recentIntents,
            RecentSwaps = recentSwaps
        });
    }

    [HttpPost("stores/{storeId}/show-private-key")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ShowPrivateKey(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var wallet = await walletStorage.GetWalletById(config!.WalletId);
        if (wallet?.Secret == null)
            return NotFound();

        return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
        {
            ReturnUrl = Url.Action(nameof(StoreOverview), new { storeId }),
            IsStored = true,
            RequireConfirm = false,
            CryptoCode = "ARK",
            Mnemonic = wallet.Secret
        });
    }

    /// <summary>
    /// Receive page: shows existing manual receive address or prompts to generate one.
    /// </summary>
    [HttpGet("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var model = new ArkReceiveViewModel();

        try
        {
            var existingAddress = await FindManualReceiveAddress(config!.WalletId!, cancellationToken);
            if (existingAddress != null)
                model.Address = existingAddress;
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to check receive address: {ex.Message}";
        }

        return View(model);
    }

    [HttpPost("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, string command, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contract = await contractService.DeriveContract(
                config!.WalletId!,
                NextContractPurpose.Receive,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = "manual" },
                cancellationToken: cancellationToken);

            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
            var address = contract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);

            return View(new ArkReceiveViewModel { Address = address });
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to generate receive address: {ex.Message}";
        }

        return RedirectToAction(nameof(Receive), new { storeId });
    }

    private async Task<string?> FindManualReceiveAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            cancellationToken: cancellationToken);

        var manualContract = existingContracts
            .FirstOrDefault(c =>
                c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate &&
                c.Metadata?.GetValueOrDefault("Source") == "manual");

        if (manualContract == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var script = Script.FromHex(manualContract.Script);
        var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
        var arkAddr = ArkAddress.FromScriptPubKey(script, serverKey);
        return arkAddr.ToString(terms.Network.ChainName == ChainName.Mainnet);
    }

    /// <summary>
    /// Legacy redirect - SpendOverview now redirects to Send wizard.
    /// </summary>
    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult SpendOverview(string storeId, string[]? destinations, string? vtxoOutpoints)
    {
        // Convert old parameters to new format
        var vtxos = vtxoOutpoints;
        var destinationsParam = destinations != null && destinations.Length > 0
            ? string.Join(",", destinations)
            : null;

        return RedirectToAction(nameof(Send), new { storeId, vtxos, destinations = destinationsParam });
    }

    private async Task<IntentBuilderViewModel> BuildIntentBuilderViewModel(
        string storeId,
        string walletId,
        string vtxoOutpointsRaw,
        bool isIntent,
        ArkBalancesViewModel balances,
        CancellationToken token)
    {
        var model = new IntentBuilderViewModel
        {
            StoreId = storeId,
            IsIntent = isIntent,
            VtxoOutpointsRaw = vtxoOutpointsRaw,
            Balances = balances,
            LightningAvailable = true // TODO: Check if Lightning is configured
        };

        // Parse outpoints and load VTXO details
        var outpointStrings = vtxoOutpointsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsedOutpoints = ParseOutpoints(outpointStrings);

        var selectedVtxos = await vtxoStorage.GetVtxos(
            outpoints: parsedOutpoints.ToList(),
            walletIds: [walletId],
            includeSpent: true,
            cancellationToken: token);

        foreach (var vtxo in selectedVtxos)
        {
            model.SelectedVtxos.Add(new SelectedVtxoViewModel
            {
                Outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}",
                TransactionId = vtxo.TransactionId,
                OutputIndex = vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                ExpiresAt = vtxo.ExpiresAt,
                IsRecoverable = vtxo.Swept,
                CanSpendOffchain = !vtxo.IsSpent() && !vtxo.Swept
            });
        }

        model.TotalSelectedAmount = model.SelectedVtxos.Sum(v => v.Amount);

        return model;
    }

    [HttpPost("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(SpendOverviewViewModel model, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model.Destination))
            return BadRequest();

        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var disposableLock = default(IDisposable);
        try
        {
            var payout = Uri.TryCreate(model.Destination, UriKind.Absolute, out var uriResult)
                ? uriResult.ParseQueryString().Get("payout")
                : null;
            if (!string.IsNullOrEmpty(payout))
            {
                disposableLock = await arkPayoutHandler.PayoutLocker.LockOrNullAsync(payout, 0, token);
                if (disposableLock is null)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Payment failed: the payout is locked";
                    return RedirectToAction(nameof(SpendOverview),
                        new {storeId = store.Id, destinations = model.PrefilledDestination});

                }
            }

            var maybeProof = await arkadeSpendingService.Spend(store, model.Destination, token);
            //check if destination is a uri and if it has a payout querystring, extract value
            if (!string.IsNullOrEmpty(payout))
            {
                var proof = new ArkPayoutProof()
                {
                    TransactionId = uint256.Parse(maybeProof),
                    DetectedInBackground = false
                };
                var result = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest()
                {
                    PayoutId = payout,
                    Proof = arkPayoutHandler.SerializeProof(proof)
                });

                TempData[WellKnownTempData.SuccessMessage] =
                    $"Payment sent to {model.Destination} with payout {payout} result {result}";
            }
            else
            {

                TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {model.Destination}";
            }

            model.PrefilledDestination.Remove(model.Destination);
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        catch (IncompleteArkadeSetupException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: incomplete arkade setup!";
            return RedirectToAction(nameof(InitialSetup), new {storeId = store.Id});
        }
        catch (MalformedPaymentDestination e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: malfomed destination!";
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        catch (ArkadePaymentFailedException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: reason: {e.Message}";
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        finally
        {
            if(disposableLock is not null)
            {
                disposableLock.Dispose();
            }
        }
    }

    [HttpPost("stores/{storeId}/build-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> BuildIntent(string storeId, IntentBuilderViewModel model, CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return errorResult;

        // Get the selected coins
        var outpointStrings = model.VtxoOutpointsRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var selectedCoins = await GetCoinsForOutpoints(config!.WalletId!, outpointStrings.ToList(), token);

        if (selectedCoins.Count == 0)
        {
            model.Errors.Add("No valid VTXOs selected.");
            model.Balances = await GetArkBalances(config.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }

        var totalInputAmount = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);

        // Get valid outputs (non-empty destinations)
        var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();

        // Check for Lightning - only single output allowed
        var lightningOutputs = validOutputs.Where(o =>
            o.Destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
            o.Destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)).ToList();

        if (lightningOutputs.Any() && validOutputs.Count > 1)
        {
            model.Errors.Add("Lightning payments only support a single output.");
            model.Balances = await GetArkBalances(config!.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }

        try
        {
            // If single Lightning output, use existing spend flow
            if (lightningOutputs.Count == 1)
            {
                var lnDestination = lightningOutputs[0].Destination
                    .Replace("lightning:", "", StringComparison.OrdinalIgnoreCase);
                await arkadeSpendingService.Spend(store!, lnDestination, token);
                TempData[WellKnownTempData.SuccessMessage] = "Lightning payment initiated successfully.";
                return RedirectToAction(nameof(Vtxos), new { storeId });
            }

            // Build ArkTxOut array from outputs
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var arkOutputs = new List<ArkTxOut>();

            foreach (var output in validOutputs)
            {
                var parseResult = ParseOutputDestination(output, serverInfo.Network);
                if (parseResult.Destination == null)
                {
                    output.Error = "Invalid destination address.";
                    model.Errors.Add($"Invalid destination: {output.Destination}");
                    continue;
                }

                // Amount priority: destination-specified > user-specified > (single output: send all)
                var outputAmount = parseResult.Amount ?? (output.AmountBtc.HasValue ? Money.Coins(output.AmountBtc.Value) : null);

                if (outputAmount == null || outputAmount == Money.Zero)
                {
                    if (validOutputs.Count == 1)
                    {
                        // Single output with no amount specified anywhere - send all
                        outputAmount = Money.Satoshis(totalInputAmount);
                    }
                    else
                    {
                        // Multi-output requires explicit amount
                        output.Error = "Amount is required.";
                        model.Errors.Add($"Amount is required for output: {output.Destination}");
                        continue;
                    }
                }

                // Output type is determined by address type:
                // - Ark address = VTXO (offchain)
                // - Bitcoin address = Onchain
                arkOutputs.Add(new ArkTxOut(parseResult.OutputType, outputAmount, parseResult.Destination));
            }

            if (model.Errors.Any())
            {
                model.Balances = await GetArkBalances(config.WalletId!, token);
                await ReloadSelectedVtxos(model, config.WalletId!, token);
                return View("IntentBuilder", model);
            }

            // Execute the spend with selected coins
            // If no outputs specified, SpendingService will send everything as change to self
            var txId = await arkadeSpender.Spend(config.WalletId!, selectedCoins.ToArray(), arkOutputs.ToArray(), token);

            // Poll for VTXO updates
            var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
            await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), token);

            TempData[WellKnownTempData.SuccessMessage] = $"Successfully joined batch. Your VTXOs will be updated in the next round. Transaction ID: {txId}";

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            model.Errors.Add($"Failed to build: {ex.Message}");
            model.Balances = await GetArkBalances(config!.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }
    }

    private (IDestination? Destination, Money? Amount, ArkTxOutType OutputType) ParseOutputDestination(SpendOutputViewModel output, Network network)
    {
        var destination = output.Destination.Trim();

        // Try direct Ark address -> VTXO output
        if (ArkAddress.TryParse(destination, out var arkAddress))
        {
            return (arkAddress, null, ArkTxOutType.Vtxo);
        }

        // Try direct Bitcoin address -> Onchain output
        try
        {
            var btcAddress = BitcoinAddress.Create(destination, network);
            return (btcAddress, null, ArkTxOutType.Onchain);
        }
        catch
        {
            // Not a valid Bitcoin address, continue
        }

        // Try BIP21 URI
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.OrdinalIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            // Check for ark parameter in query string -> VTXO output
            if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsArkAddress))
            {
                var amount = qs["amount"] is { } amountStr && decimal.TryParse(amountStr, System.Globalization.CultureInfo.InvariantCulture, out var amountDec)
                    ? Money.Coins(amountDec)
                    : null;
                return (qsArkAddress, amount, ArkTxOutType.Vtxo);
            }

            // Try host as Ark address -> VTXO output
            if (ArkAddress.TryParse(host, out var hostArkAddress))
            {
                var amount = qs["amount"] is { } amountStr && decimal.TryParse(amountStr, System.Globalization.CultureInfo.InvariantCulture, out var amountDec)
                    ? Money.Coins(amountDec)
                    : null;
                return (hostArkAddress, amount, ArkTxOutType.Vtxo);
            }

            // Try host as Bitcoin address -> Onchain output
            try
            {
                var btcAddress = BitcoinAddress.Create(host, network);
                var amount = qs["amount"] is { } amountStr && decimal.TryParse(amountStr, System.Globalization.CultureInfo.InvariantCulture, out var amountDec)
                    ? Money.Coins(amountDec)
                    : null;
                return (btcAddress, amount, ArkTxOutType.Onchain);
            }
            catch
            {
                // Not a valid Bitcoin address
            }
        }

        return (null, null, ArkTxOutType.Vtxo);
    }

    private async Task ReloadSelectedVtxos(IntentBuilderViewModel model, string walletId, CancellationToken token)
    {
        model.SelectedVtxos.Clear();
        if (string.IsNullOrEmpty(model.VtxoOutpointsRaw)) return;

        var outpointStrings = model.VtxoOutpointsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsedOutpoints = ParseOutpoints(outpointStrings);

        var selectedVtxos = await vtxoStorage.GetVtxos(
            outpoints: parsedOutpoints.ToList(),
            walletIds: [walletId],
            includeSpent: true,
            cancellationToken: token);

        foreach (var vtxo in selectedVtxos)
        {
            model.SelectedVtxos.Add(new SelectedVtxoViewModel
            {
                Outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}",
                TransactionId = vtxo.TransactionId,
                OutputIndex = vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                ExpiresAt = vtxo.ExpiresAt,
                IsRecoverable = vtxo.Swept,
                CanSpendOffchain = !vtxo.IsSpent() && !vtxo.Swept
            });
        }

        model.TotalSelectedAmount = model.SelectedVtxos.Sum(v => v.Amount);
    }

    [HttpPost("stores/{storeId}/estimate-fees")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EstimateFees(string storeId, [FromBody] FeeEstimateRequest request, CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return BadRequest("Invalid store configuration");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var response = new FeeEstimateResponse();

            // Check if this is a Lightning payment
            if (request.Outputs.Count == 1)
            {
                var dest = request.Outputs[0].Destination?.Trim() ?? "";
                if (IsLightningDestination(dest))
                {
                    // Lightning swap fees
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                        if (limits != null)
                        {
                            var amount = request.Outputs[0].AmountSats ?? request.TotalInputSats;

                            response.IsLightning = true;
                            response.FeePercentage = limits.SubmarineFeePercentage * 100; // Convert to percentage for display
                            response.MinerFeeSats = limits.SubmarineMinerFee;
                            response.EstimatedFeeSats = (long)Math.Ceiling(amount * limits.SubmarineFeePercentage) + limits.SubmarineMinerFee;
                            response.FeeDescription = $"{limits.SubmarineFeePercentage * 100:F2}% + {limits.SubmarineMinerFee} sats miner fee";
                        }
                        else
                        {
                            response.Error = "Failed to fetch Boltz limits";
                        }
                    }
                    else
                    {
                        response.Error = "Lightning swaps not available";
                    }

                    return Json(response);
                }
            }

            // Ark intent/transaction fees - need to get coins and build outputs
            var isAutoMode = string.Equals(request.CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);
            List<ArkCoin> coins;

            if (isAutoMode)
            {
                // Auto mode: use smart coin selection based on destination type
                var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);
                var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
                var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
                var availableCoins = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();

                if (!availableCoins.Any())
                {
                    response.Error = "No spendable coins available";
                    return Json(response);
                }

                // Determine destination type for smart selection
                var destType = DestinationType.ArkAddress; // default: consolidation / ark send
                long? targetSats = null;

                if (request.Outputs.Any(o => !string.IsNullOrWhiteSpace(o.Destination)))
                {
                    var firstDest = request.Outputs.First(o => !string.IsNullOrWhiteSpace(o.Destination)).Destination!.Trim();
                    if (IsLightningDestination(firstDest))
                        destType = DestinationType.LightningInvoice;
                    else if (firstDest.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("1") || firstDest.StartsWith("3"))
                        destType = DestinationType.BitcoinAddress;

                    // Calculate target amount
                    var amounts = request.Outputs.Where(o => o.AmountSats.HasValue).Select(o => o.AmountSats!.Value).ToList();
                    if (amounts.Any())
                        targetSats = amounts.Sum();
                }

                // Reuse the same selection logic as SuggestCoins
                var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
                var recoverable = availableCoins.Where(c => c.Swept).ToList();
                SuggestCoinsResponse suggestion;

                if (destType == DestinationType.LightningInvoice)
                {
                    suggestion = SelectCoins(nonRecoverable.Any() ? nonRecoverable : availableCoins, targetSats, SpendType.Swap);
                }
                else if (destType == DestinationType.BitcoinAddress)
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else if (string.Equals(request.SpendType, "Batch", StringComparison.OrdinalIgnoreCase))
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else
                {
                    // Ark address / offchain: prefer non-recoverable
                    suggestion = nonRecoverable.Any()
                        ? SelectCoins(nonRecoverable, targetSats, SpendType.Offchain)
                        : SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }

                if (suggestion.Error != null)
                {
                    response.Error = suggestion.Error;
                    return Json(response);
                }

                // Map selected outpoints back to coins
                var selectedSet = suggestion.SuggestedOutpoints.ToHashSet();
                coins = availableCoins.Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}")).ToList();

                // Populate response with selected coin info
                response.TotalInputSats = coins.Sum(c => c.TxOut.Value.Satoshi);
                response.SelectedCoinCount = coins.Count;
                response.SelectedOutpoints = suggestion.SuggestedOutpoints;

                request.TotalInputSats = response.TotalInputSats;
            }
            else
            {
                coins = await GetCoinsForOutpoints(config!.WalletId!, request.VtxoOutpoints, token);
            }

            if (coins.Count == 0)
            {
                response.Error = "No valid coins found for selected outpoints";
                return Json(response);
            }

            var outputs = new List<ArkTxOut>();
            foreach (var outputReq in request.Outputs)
            {
                if (string.IsNullOrWhiteSpace(outputReq.Destination)) continue;

                var parseResult = ParseOutputDestination(new SpendOutputViewModel { Destination = outputReq.Destination }, serverInfo.Network);
                if (parseResult.Destination == null) continue;

                var amount = outputReq.AmountSats.HasValue
                    ? Money.Satoshis(outputReq.AmountSats.Value)
                    : (request.Outputs.Count == 1 ? Money.Satoshis(request.TotalInputSats) : Money.Zero);

                if (amount > Money.Zero)
                {
                    outputs.Add(new ArkTxOut(parseResult.OutputType, amount, parseResult.Destination));
                }
            }

            // If no outputs specified, this is a consolidation (send to self)
            // For fee estimation, we use a placeholder - fee is based on input/output amounts and types
            if (outputs.Count == 0)
            {
                var totalInput = coins.Sum(c => c.TxOut.Value);
                // Use first coin's contract address as placeholder for fee estimation
                // The actual destination will be derived at spend time
                var placeholderDest = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, totalInput, placeholderDest));
            }

            // For batch with on-chain outputs, include a change VTXO output for accurate fee estimation
            var hasOnchain = outputs.Any(o => o.Type == ArkTxOutType.Onchain);
            var totalOutputSats = outputs.Sum(o => o.Value.Satoshi);
            var totalCoinsSats = coins.Sum(c => c.TxOut.Value.Satoshi);
            if (hasOnchain && totalCoinsSats > totalOutputSats)
            {
                var changePlaceholder = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(totalCoinsSats - totalOutputSats), changePlaceholder));
            }

            // Estimate the fee
            var estimatedFee = await feeEstimator.EstimateFeeAsync(coins.ToArray(), outputs.ToArray(), token);
            response.EstimatedFeeSats = estimatedFee;
            response.FeeDescription = hasOnchain ? "Batch transaction fee" : "Ark service fee";

            return Json(response);
        }
        catch (Exception ex)
        {
            return Json(new FeeEstimateResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Parse a destination string server-side (BIP21, Lightning, Ark address).
    /// Used by Send wizard AJAX for rich destination display.
    /// </summary>
    [HttpPost("stores/{storeId}/parse-destination")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ParseDestination(
        string storeId,
        [FromBody] ParseDestinationRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return BadRequest("Invalid store configuration");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = await ParseSend2DestinationAsync(request.Destination, request.AmountBtc, serverInfo.Network, token);

            return Json(new ParseDestinationResponse
            {
                RawBip21 = parsed.RawDestination,
                ResolvedAddress = parsed.ResolvedAddress,
                Type = parsed.Type.ToString(),
                TypeBadge = parsed.TypeBadge,
                TypeBadgeClass = parsed.TypeBadgeClass,
                AmountSats = parsed.AmountSats,
                AmountBtc = parsed.AmountBtc,
                PayoutId = parsed.PayoutId,
                IsValid = parsed.IsValid,
                Error = parsed.Error,
                IsBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                          || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase),
                IsLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning
                              or Send2DestinationType.Lnurl,
                IsLnurl = parsed.Type == Send2DestinationType.Lnurl,
                LnurlMinSats = parsed.LnurlMinSats,
                LnurlMaxSats = parsed.LnurlMaxSats,
            });
        }
        catch (Exception ex)
        {
            return Json(new ParseDestinationResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Suggests optimal coin selection based on destination type and amount.
    /// </summary>
    [HttpPost("stores/{storeId}/suggest-coins")]
    public async Task<IActionResult> SuggestCoins(
        string storeId,
        [FromBody] SuggestCoinsRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return Json(new SuggestCoinsResponse { Error = "Store not configured" });

        try
        {
            var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);

            // Exclude VTXOs locked by pending intents
            var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
            var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);

            // Filter out excluded outpoints and locked VTXOs
            var excludeSet = request.ExcludeOutpoints?
                .Select(o => o.Trim())
                .ToHashSet() ?? new HashSet<string>();

            var availableCoins = allCoins
                .Where(c => !lockedSet.Contains(c.Outpoint) && !excludeSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
                .ToList();

            if (!availableCoins.Any())
            {
                return Json(new SuggestCoinsResponse { Error = "No spendable coins available" });
            }

            // Separate by recoverability
            var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
            var recoverable = availableCoins.Where(c => c.Swept).ToList();

            var response = new SuggestCoinsResponse();

            // Lightning requires non-recoverable coins only
            if (request.DestinationType == DestinationType.LightningInvoice)
            {
                if (!nonRecoverable.Any())
                {
                    return Json(new SuggestCoinsResponse
                    {
                        Error = "Lightning requires non-recoverable coins. No non-recoverable coins available."
                    });
                }

                response = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Swap);
            }
            // Ark address: prefer offchain (non-recoverable), fallback to batch (recoverable)
            else if (request.DestinationType == DestinationType.ArkAddress)
            {
                // Try offchain first with non-recoverable
                if (nonRecoverable.Any())
                {
                    var offchainAttempt = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Offchain);
                    if (offchainAttempt.Error == null)
                    {
                        response = offchainAttempt;
                    }
                    else if (recoverable.Any())
                    {
                        // Fallback to batch with all coins
                        response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
                        response.Warning = "Using batch mode (recoverable coins included)";
                    }
                    else
                    {
                        response = offchainAttempt; // Return the error
                    }
                }
                else if (recoverable.Any())
                {
                    // Only recoverable available - must use batch
                    response = SelectCoins(recoverable, request.AmountSats, SpendType.Batch);
                    response.Warning = "Offchain not available - only recoverable coins";
                }
                else
                {
                    response.Error = "No spendable coins available";
                }
            }
            // Bitcoin address: always batch
            else
            {
                response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
            }

            return Json(response);
        }
        catch (Exception ex)
        {
            return Json(new SuggestCoinsResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-flight validation before executing spend.
    /// </summary>
    [HttpPost("stores/{storeId}/validate-spend")]
    public async Task<IActionResult> ValidateSpend(
        string storeId,
        [FromBody] ValidateSpendRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return Json(new ValidateSpendResponse { Errors = { "Store not configured" } });

        var response = new ValidateSpendResponse();
        var hasLightning = false;
        var hasRecoverableCoins = false;

        // Validate coins exist and are spendable
        if (request.VtxoOutpoints.Any())
        {
            var outpoints = ParseOutpoints(request.VtxoOutpoints.ToArray());

            var vtxos = await vtxoStorage.GetVtxos(
                walletIds: [config!.WalletId!],
                outpoints: outpoints.ToList(),
                includeSpent: false,
                cancellationToken: token);

            if (vtxos.Count != request.VtxoOutpoints.Count)
            {
                response.Errors.Add("Some selected coins are no longer available");
            }

            hasRecoverableCoins = vtxos.Any(v => v.Swept);
        }
        else
        {
            response.Errors.Add("No coins selected");
        }

        // Get network for address parsing
        var serverInfo = await clientTransport.GetServerInfoAsync(token);
        var network = serverInfo.Network;

        // Validate each output
        for (int i = 0; i < request.Outputs.Count; i++)
        {
            var output = request.Outputs[i];
            var result = new OutputValidationResult { Index = i };

            if (string.IsNullOrWhiteSpace(output.Destination))
            {
                result.Error = "Destination required";
            }
            else
            {
                var destination = output.Destination.Trim();

                // Check for Lightning first (BOLT11, LNURL, Lightning Address)
                if (IsLightningDestination(destination))
                {
                    result.DetectedType = destination.IsValidEmail() ||
                        destination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase)
                        ? DestinationType.LnurlPay
                        : DestinationType.LightningInvoice;
                    hasLightning = true;
                }
                else
                {
                    // Use existing ParseOutputDestination helper
                    var spendOutput = new SpendOutputViewModel { Destination = destination };
                    var (dest, amount, outputType) = ParseOutputDestination(spendOutput, network);

                    if (dest == null)
                    {
                        result.Error = "Invalid address format";
                    }
                    else if (outputType == ArkTxOutType.Vtxo)
                    {
                        result.DetectedType = DestinationType.ArkAddress;
                    }
                    else
                    {
                        result.DetectedType = DestinationType.BitcoinAddress;
                    }
                }
            }

            response.OutputResults.Add(result);
        }

        // Cross-validation rules
        if (hasLightning)
        {
            if (request.Outputs.Count > 1)
            {
                response.Errors.Add("Lightning supports single output only");
            }
            if (hasRecoverableCoins)
            {
                response.Errors.Add("Lightning requires non-recoverable coins");
            }
            response.SpendType = SpendType.Swap;
        }
        else if (response.OutputResults.Any(r => r.DetectedType == DestinationType.BitcoinAddress))
        {
            response.SpendType = SpendType.Batch;
        }
        else if (hasRecoverableCoins)
        {
            response.SpendType = SpendType.Batch;
        }
        else
        {
            response.SpendType = SpendType.Offchain;
        }

        response.IsValid = !response.Errors.Any() && !response.OutputResults.Any(r => r.Error != null);
        return Json(response);
    }

    /// <summary>
    /// Unified Send Wizard - main entry point.
    /// </summary>
    [HttpGet("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        string? vtxos,
        string? destinations,
        string? destination,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        var model = new SendWizardViewModel
        {
            StoreId = storeId,
            VtxoOutpoints = vtxos,
            Destinations = destinations,
            Destination = destination
        };

        // Load balances
        model.Balances = await GetArkBalances(config!.WalletId!, token);

        // Load available (spendable) coins - get outpoints from ArkCoin, then fetch ArkVtxo details
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);

        // Exclude VTXOs locked by pending intents
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var spendableOutpoints = allCoins
            .Where(c => !lockedSet.Contains(c.Outpoint))
            .Select(c => c.Outpoint).ToList();

        if (!spendableOutpoints.Any())
            return View("Send", model);

        // Fetch full ArkVtxo details for the spendable coins
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        if (!model.AvailableVtxos.Any())
            return View("Send", model);

        // Handle pre-selected VTXOs from query param
        if (!string.IsNullOrEmpty(vtxos))
        {
            if (vtxos.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // Special case: select all available VTXOs
                model.SelectedVtxos = model.AvailableVtxos.ToList();
                model.CoinSelectionMode = "manual";
            }
            else
            {
                var requestedOutpoints = vtxos.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToHashSet();

                model.SelectedVtxos = model.AvailableVtxos
                    .Where(v => requestedOutpoints.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
                    .ToList();

                model.CoinSelectionMode = "manual";

                // Warn if some requested coins unavailable
                if (model.SelectedVtxos.Count < requestedOutpoints.Count)
                {
                    var found = model.SelectedVtxos
                        .Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}")
                        .ToHashSet();
                    var missing = requestedOutpoints.Except(found).Count();
                    model.Errors.Add($"{missing} selected coin(s) no longer available");
                }
            }
        }

        // Handle pre-filled destinations (BIP21-aware parsing)
        if (!string.IsNullOrEmpty(destinations))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsedDestinations = ParseDestinationsParam(destinations, serverInfo.Network);

            foreach (var parsed in parsedDestinations)
            {
                var isBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                              || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
                var output = new SendOutputViewModel
                {
                    Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                    RawBip21 = isBip21 ? parsed.RawDestination : null,
                    ResolvedAddress = parsed.ResolvedAddress,
                    AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                    PayoutId = parsed.PayoutId,
                    IsBip21Parsed = isBip21,
                    IsReadonly = isBip21,
                    DetectedType = MapSend2TypeToDestinationType(parsed.Type),
                    IsLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning,
                    Error = parsed.Error
                };
                model.Outputs.Add(output);
            }
        }
        else if (!string.IsNullOrEmpty(destination))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = ParseSend2Destination(destination, null, serverInfo.Network);
            var isBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                          || destination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
            var isLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning;

            model.Outputs.Add(new SendOutputViewModel
            {
                Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                RawBip21 = isBip21 ? destination : null,
                ResolvedAddress = parsed.ResolvedAddress,
                AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                PayoutId = parsed.PayoutId,
                IsBip21Parsed = isBip21,
                IsReadonly = isBip21 || isLightning,
                DetectedType = MapSend2TypeToDestinationType(parsed.Type),
                IsLightning = isLightning,
                Error = parsed.Error
            });
        }
        else
        {
            // Default: one empty output row
            model.Outputs.Add(new SendOutputViewModel());
        }

        return View("Send", model);
    }

    /// <summary>
    /// Execute the send transaction.
    /// </summary>
    [HttpPost("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        [FromForm] SendWizardViewModel model,
        [FromForm] string[] selectedVtxoOutpoints,
        [FromForm] string? SpendType,
        [FromForm] string? CoinSelectionMode,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        model.StoreId = storeId;
        model.Balances = await GetArkBalances(config!.WalletId!, token);

        // User's spend type preference (Arkade = offchain, Batch = onchain intent)
        var preferBatch = string.Equals(SpendType, "Batch", StringComparison.OrdinalIgnoreCase);

        // Re-load available coins for validation (excluding locked VTXOs)
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var unlocked = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();
        var spendableOutpoints = unlocked.Select(c => c.Outpoint).ToList();
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        // Validate selected coins
        var isAutoMode = string.Equals(CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);

        if (!selectedVtxoOutpoints.Any() && !isAutoMode)
        {
            model.Errors.Add("No coins selected");
            return View("Send", model);
        }

        var selectedSet = selectedVtxoOutpoints.ToHashSet();
        var selectedCoins = unlocked
            .Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
            .ToList();

        if (selectedCoins.Count != selectedVtxoOutpoints.Length && isAutoMode)
        {
            // Auto mode: re-select coins from available unlocked set
            selectedCoins = unlocked.ToList();
            selectedSet = selectedCoins
                .Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}")
                .ToHashSet();
        }
        else if (selectedCoins.Count != selectedVtxoOutpoints.Length)
        {
            var missing = selectedVtxoOutpoints.Length - selectedCoins.Count;
            model.Errors.Add($"{missing} selected coin(s) are no longer available (spent or locked). Please re-select your coins and try again.");
            return View("Send", model);
        }

        if (!selectedCoins.Any())
        {
            model.Errors.Add("No coins available to spend");
            return View("Send", model);
        }

        model.SelectedVtxos = model.AvailableVtxos
            .Where(v => selectedSet.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
            .ToList();

        // Validate outputs - allow empty for consolidation
        var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();
        var isConsolidation = !validOutputs.Any();

        // Handle consolidation (no destination = send to self)
        if (isConsolidation)
        {
            try
            {
                var consolidationServerInfo = await clientTransport.GetServerInfoAsync(token);
                var consolidationTotalInput = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
                var hasRecoverableCoins = selectedCoins.Any(c => c.Swept);

                // Prevent pointless 1-in-1-out Arkade consolidation
                // With Arkade (not Batch) and only 1 non-recoverable coin, consolidation does nothing useful
                if (!preferBatch && !hasRecoverableCoins && selectedCoins.Count == 1)
                {
                    model.Errors.Add("Arkade consolidation with a single coin is not useful. Either select multiple coins to consolidate, use Batch mode to renew expiry, or enter a destination to send funds.");
                    return View("Send", model);
                }

                // Get the wallet's own Ark address for consolidation
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.Inactive, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // For recoverable coins OR user chose Batch, create an intent (batch transaction)
                if (hasRecoverableCoins || preferBatch)
                {
                    // Estimate fee for batch transaction
                    var consolidationOutputForFee = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(consolidationTotalInput),
                        selfDest);
                    var feeEstimation = await feeEstimator.EstimateFeeAsync(
                        selectedCoins.ToArray(),
                        new[] { consolidationOutputForFee },
                        token);

                    var outputAmount = consolidationTotalInput - feeEstimation;
                    if (outputAmount <= 0)
                    {
                        model.Errors.Add("Insufficient funds after fees");
                        return View("Send", model);
                    }

                    var consolidationOutput = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(outputAmount),
                        selfDest);

                    // Create intent for batch (automatically cancels any overlapping intents)
                    var intentTxId = await intentGenerationService.GenerateManualIntent(
                        config.WalletId!,
                        new ArkIntentSpec(
                            selectedCoins.ToArray(),
                            new [] { consolidationOutput },
                            null,
                            null
                        ),
                        cancellationToken: token);

                    var message = hasRecoverableCoins
                        ? $"Recovery intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round."
                        : $"Batch intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round.";

                    return RedirectWithSuccess(nameof(Intents), message, new { storeId });
                }

                // For non-recoverable coins with Arkade preference, use direct Arkade spend
                var arkadeOutput = new ArkTxOut(
                    ArkTxOutType.Vtxo,
                    Money.Satoshis(consolidationTotalInput),
                    selfDest);

                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    selectedCoins.ToArray(),
                    new[] { arkadeOutput },
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), token);

                return RedirectWithSuccess(nameof(StoreOverview), $"Coins consolidated successfully! TxId: {txId}", new { storeId });
            }
            catch (Exception ex)
            {
                model.Errors.Add($"Consolidation failed: {ex.Message}");
                return View("Send", model);
            }
        }

        // Get server info for network (needed for Lightning and destination parsing)
        var serverInfo = await clientTransport.GetServerInfoAsync(token);

        // Check for Lightning (BOLT11, LNURL, or Lightning Address)
        var isLightning = validOutputs.Any(o => IsLightningDestination(o.Destination));

        if (isLightning)
        {
            if (validOutputs.Count > 1)
            {
                model.Errors.Add("Lightning supports single output only");
                return View("Send", model);
            }

            if (selectedCoins.Any(c => c.Swept))
            {
                model.Errors.Add("Lightning requires non-recoverable coins");
                return View("Send", model);
            }

            // Execute Lightning payment
            try
            {
                var lnOutput = validOutputs[0];
                var lnDestination = lnOutput.Destination;

                // Resolve LNURL/Lightning Address to BOLT11 at submit time
                if (lnDestination.IsValidEmail() ||
                    lnDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = lnOutput.AmountSats ?? model.TotalSelectedSats;
                    var (bolt11, lnurlError) = await ResolveLnurlToInvoiceAsync(
                        lnDestination, amount, serverInfo.Network, token);
                    if (lnurlError != null)
                    {
                        model.Errors.Add($"LNURL resolution failed: {lnurlError}");
                        return View("Send", model);
                    }
                    lnDestination = bolt11!;
                }
                else
                {
                    lnDestination = lnDestination
                        .Replace("lightning:", "", StringComparison.OrdinalIgnoreCase);
                }

                await arkadeSpendingService.Spend(store!, lnDestination, token);

                // Mark payout as paid if this fulfills a payout
                if (!string.IsNullOrEmpty(lnOutput.PayoutId))
                    await MarkPayoutPaid(lnOutput.PayoutId, null, token);

                return RedirectWithSuccess(nameof(StoreOverview), "Lightning payment sent!", new { storeId });
            }
            catch (Exception ex)
            {
                model.Errors.Add($"Lightning payment failed: {ex.Message}");
                return View("Send", model);
            }
        }

        // Parse all destinations and build ArkTxOut array
        var totalInputAmount = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
        var arkOutputs = new List<ArkTxOut>();

        for (int i = 0; i < validOutputs.Count; i++)
        {
            var output = validOutputs[i];
            var spendOutput = new SpendOutputViewModel { Destination = output.Destination };
            var (dest, parsedAmount, outputType) = ParseOutputDestination(spendOutput, serverInfo.Network);

            if (dest == null)
            {
                output.Error = "Invalid address format";
                model.Errors.Add($"Output {i + 1}: Invalid address format");
                continue;
            }

            // Amount priority: user-specified > destination-specified > (single output: send all)
            var outputAmount = output.AmountSats.HasValue
                ? Money.Satoshis(output.AmountSats.Value)
                : parsedAmount;

            if (outputAmount == null || outputAmount == Money.Zero)
            {
                if (validOutputs.Count == 1)
                {
                    // Single output with no amount - send all
                    outputAmount = Money.Satoshis(totalInputAmount);
                }
                else
                {
                    output.Error = "Amount is required";
                    model.Errors.Add($"Output {i + 1}: Amount is required");
                    continue;
                }
            }

            arkOutputs.Add(new ArkTxOut(outputType, outputAmount, dest));
        }

        if (model.Errors.Any())
        {
            return View("Send", model);
        }

        // Determine if batch is required (on-chain outputs or user preference)
        var hasOnchainOutput = arkOutputs.Any(o => o.Type == ArkTxOutType.Onchain);
        var useBatch = preferBatch || hasOnchainOutput;

        // Execute the spend
        try
        {
            if (useBatch)
            {
                // Batch path: create an intent for the next batch round
                // Need to add a change output back to self for the remainder after fees
                var totalOutput = arkOutputs.Sum(o => o.Value.Satoshi);

                // Build preliminary outputs to estimate fees (include a placeholder change output)
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // Estimate fees with all outputs including change
                var preliminaryOutputs = arkOutputs.ToList();
                var preliminaryChange = totalInputAmount - totalOutput;
                if (preliminaryChange > 0)
                {
                    preliminaryOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(preliminaryChange), selfDest));
                }

                var feeEstimation = await feeEstimator.EstimateFeeAsync(
                    selectedCoins.ToArray(),
                    preliminaryOutputs.ToArray(),
                    token);

                var changeAmount = totalInputAmount - totalOutput - feeEstimation;
                if (changeAmount < 0)
                {
                    model.Errors.Add($"Insufficient funds. Need {totalOutput + feeEstimation} sats but only have {totalInputAmount} sats.");
                    return View("Send", model);
                }

                // Build final outputs: destination(s) + change (if any)
                var finalOutputs = arkOutputs.ToList();
                if (changeAmount > 0)
                {
                    finalOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(changeAmount), selfDest));
                }

                var intentTxId = await intentGenerationService.GenerateManualIntent(
                    config.WalletId!,
                    new ArkIntentSpec(
                        selectedCoins.ToArray(),
                        finalOutputs.ToArray(),
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddHours(1)
                    ),
                    cancellationToken: token);

                // Mark payouts as paid if any outputs fulfill payouts (no txId yet — assigned at batch time)
                foreach (var output in validOutputs.Where(o => !string.IsNullOrEmpty(o.PayoutId)))
                {
                    await MarkPayoutPaid(output.PayoutId!, null, token);
                }

                return RedirectWithSuccess(nameof(Intents),
                    $"Batch intent created! Intent ID: {intentTxId}. Transaction will be included in the next batch round.",
                    new { storeId });
            }
            else
            {
                // Arkade path: instant offchain spend
                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    selectedCoins.ToArray(),
                    arkOutputs.ToArray(),
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), token);

                // Mark payouts as paid if any outputs fulfill payouts
                foreach (var output in validOutputs.Where(o => !string.IsNullOrEmpty(o.PayoutId)))
                {
                    await MarkPayoutPaid(output.PayoutId!, txId, token);
                }

                return RedirectWithSuccess(nameof(StoreOverview), $"Transaction sent successfully! TxId: {txId}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            model.Errors.Add($"Transaction failed: {ex.Message}");
            return View("Send", model);
        }
    }

    private static SuggestCoinsResponse SelectCoins(
        List<ArkCoin> coins,
        long? targetSats,
        SpendType spendType)
    {
        if (!coins.Any())
        {
            return new SuggestCoinsResponse { Error = "No coins available" };
        }

        // Sort by amount descending for efficient selection
        var sorted = coins.OrderByDescending(c => c.TxOut.Value.Satoshi).ToList();

        // If no target, select all (send-all mode)
        if (!targetSats.HasValue)
        {
            return new SuggestCoinsResponse
            {
                SuggestedOutpoints = sorted.Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}").ToList(),
                TotalSats = sorted.Sum(c => c.TxOut.Value.Satoshi),
                SpendType = spendType
            };
        }

        // Greedy selection to meet target
        var selected = new List<ArkCoin>();
        long total = 0;

        foreach (var coin in sorted)
        {
            selected.Add(coin);
            total += coin.TxOut.Value.Satoshi;
            if (total >= targetSats.Value)
                break;
        }

        if (total < targetSats.Value)
        {
            return new SuggestCoinsResponse
            {
                Error = $"Insufficient funds. Need {targetSats.Value} sats but only {total} sats available."
            };
        }

        return new SuggestCoinsResponse
        {
            SuggestedOutpoints = selected.Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}").ToList(),
            TotalSats = total,
            SpendType = spendType
        };
    }

    private async Task<List<ArkCoin>> GetCoinsForOutpoints(string walletId, List<string> outpoints, CancellationToken token)
    {
        var coins = new List<ArkCoin>();
        var availableCoins = await arkadeSpender.GetAvailableCoins(walletId, token);

        foreach (var outpointStr in outpoints)
        {
            var parts = outpointStr.Split(':');
            if (parts.Length != 2) continue;

            var txId = parts[0];
            if (!uint.TryParse(parts[1], out var vout)) continue;

            var coin = availableCoins.FirstOrDefault(c =>
                c.Outpoint.Hash.ToString() == txId && c.Outpoint.N == vout);

            if (coin != null)
            {
                coins.Add(coin);
            }
        }

        return coins;
    }

    [HttpPost("stores/{storeId}/update-wallet-config")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> UpdateWalletConfig(string storeId, StoreOverviewViewModel model, string? command = null, CancellationToken cancellationToken = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (command == "clear-destination")
        {
            await UpdateWalletDestinationAsync(config!.WalletId!, null, cancellationToken);
            return RedirectWithSuccess(nameof(StoreOverview), "Auto-sweep destination cleared.", new { storeId });
        }

        if (command == "save" && !string.IsNullOrEmpty(model.Destination))
        {
            if (config!.AllowSubDustAmounts)
                return RedirectWithError(nameof(StoreOverview), "Cannot configure auto-sweep while sub-dust amounts are enabled. Disable sub-dust amounts first.", new { storeId });

            try
            {
                var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
                WalletFactory.ValidateDestination(model.Destination, serverInfo);
                await UpdateWalletDestinationAsync(config.WalletId!, model.Destination, cancellationToken);
                return RedirectWithSuccess(nameof(StoreOverview), "Auto-sweep destination updated.", new { storeId });
            }
            catch (Exception ex)
            {
                return RedirectWithError(nameof(StoreOverview), $"Failed to update destination: {ex.Message}", new { storeId });
            }
        }

        if (command == "toggle-subdust")
        {
            var toggleWallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);
            var destination = toggleWallet?.Destination;

            if (!config.AllowSubDustAmounts && !string.IsNullOrEmpty(destination))
                return RedirectWithError(nameof(StoreOverview), "Cannot enable sub-dust amounts while auto-sweep is configured. Clear the auto-sweep destination first.", new { storeId });

            var newConfig = config with { AllowSubDustAmounts = !config.AllowSubDustAmounts };
            store!.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
            await storeRepository.UpdateStore(store);
            return RedirectWithSuccess(nameof(StoreOverview),
                newConfig.AllowSubDustAmounts ? "Sub-dust amounts enabled for Arkade payments." : "Sub-dust amounts disabled for Arkade payments.",
                new { storeId });
        }

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpGet("stores/{storeId}/contracts")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Contracts(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool debug = false)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreContractsViewModel { StoreId = storeId });

        // Get status filter using helper
        var activeFilter = ParseBooleanFilter(searchTerm, "status", "active");

        // Get contracts with pagination
        var contracts = await contractStorage.GetContracts(
            walletIds: [config.WalletId],
            isActive: activeFilter,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs for the contracts (include spent and recoverable for full history)
        var contractVtxos = new Dictionary<string, ArkVtxo[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToList();
            var vtxos = await vtxoStorage.GetVtxos(
                scripts: contractScripts,
                walletIds: [config.WalletId],
                includeSpent: true,
                cancellationToken: HttpContext.RequestAborted);

            contractVtxos = vtxos
                .GroupBy(v => v.Script)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        // Always load swaps
        var contractSwaps = new Dictionary<string, NArk.Swaps.Models.ArkSwap[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToArray();
            var swaps = await swapStorage.GetSwaps(
                walletIds: [config.WalletId!],
                contractScripts: contractScripts,
                cancellationToken: HttpContext.RequestAborted);
            contractSwaps = swaps
                .GroupBy(s => s.ContractScript)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        var model = new StoreContractsViewModel
        {
            StoreId = storeId,
            Contracts = contracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            ContractVtxos = contractVtxos,
            ContractSwaps = contractSwaps,
            CanManageContracts = config.GeneratedByStore,
            Debug = debug,
            CachedSwapScripts = [], // Active swap scripts tracked by SwapsManagementService internally
            CachedContractScripts = (await contractStorage.GetContracts(walletIds: [config.WalletId], isActive: true, cancellationToken: HttpContext.RequestAborted))
                .Select(c => c.Script).ToHashSet(),
            ListenedScripts = debug ? vtxoSyncService.ListenedScripts.ToHashSet() : []
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/swaps")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Swaps(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool debug = false)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreSwapsViewModel { StoreId = storeId });

        // Get status filter using helper
        var statusFilter = ParseEnumFilter<ArkSwapStatus>(searchTerm, "status", s => s switch
        {
            "pending" => ArkSwapStatus.Pending,
            "settled" => ArkSwapStatus.Settled,
            "failed" => ArkSwapStatus.Failed,
            _ => null
        });

        // Get type filter using helper
        var typeFilter = ParseEnumFilter<ArkSwapType>(searchTerm, "type", t => t switch
        {
            "reverse" => ArkSwapType.ReverseSubmarine,
            "submarine" => ArkSwapType.Submarine,
            _ => null
        });

        var swaps = await swapStorage.GetSwaps(
            walletIds: [config.WalletId!],
            status: statusFilter != null ? [statusFilter.Value] : null,
            swapTypes: typeFilter != null ? [typeFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get contracts for the swaps to display contract details
        var swapContractScripts = swaps.Select(s => s.ContractScript).Distinct().ToArray();
        var swapContracts = await contractStorage.GetContracts(
            walletIds: [config.WalletId!],
            scripts: swapContractScripts,
            cancellationToken: HttpContext.RequestAborted);

        var model = new StoreSwapsViewModel
        {
            StoreId = storeId,
            Swaps = swaps,
            SwapContracts = swapContracts.ToDictionary(c => c.Script),
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            Debug = debug,
            CachedSwapIds = []
        };

        return View(model);
    }

    [HttpPost("stores/{storeId}/swaps/{swapId}/poll")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PollSwap(string storeId, string swapId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            if (boltzClient == null)
                return RedirectWithError(nameof(Swaps), "Boltz client is not configured", new { storeId });

            var swaps = await swapStorage.GetSwaps(
                walletIds: [config!.WalletId!],
                swapIds: [swapId],
                cancellationToken: HttpContext.RequestAborted);
            var swap = swaps.FirstOrDefault();
            if (swap == null)
                return RedirectWithError(nameof(Swaps), $"Swap {swapId} not found.", new { storeId });

            var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, HttpContext.RequestAborted);
            var newStatus = MapBoltzStatus(statusResponse.Status);

            if (swap.Status != newStatus)
            {
                await swapStorage.UpdateSwapStatus(config.WalletId!, swapId, newStatus, cancellationToken: HttpContext.RequestAborted);
                return RedirectWithSuccess(nameof(Swaps), $"Swap {swapId} polled successfully. Status updated to: {newStatus}", new { storeId });
            }

            return RedirectWithSuccess(nameof(Swaps), $"Swap {swapId} polled successfully. No status change (current: {swap.Status}).", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Swaps), $"Error polling swap: {ex.Message}", new { storeId });
        }
    }

    private static ArkSwapStatus MapBoltzStatus(string status)
    {
        return status switch
        {
            "swap.created" or "invoice.set" => ArkSwapStatus.Pending,
            "invoice.failedToPay" or "invoice.expired" or "swap.expired" or "transaction.failed" or "transaction.refunded" => ArkSwapStatus.Failed,
            "transaction.mempool" => ArkSwapStatus.Pending,
            "transaction.confirmed" or "invoice.settled" or "transaction.claimed" => ArkSwapStatus.Settled,
            _ => ArkSwapStatus.Unknown
        };
    }

    [HttpGet("stores/{storeId}/vtxos")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Vtxos(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreVtxosViewModel { StoreId = storeId });

        // Parse status filters - default to unspent and recoverable if no filter is set
        var search = new SearchString(searchTerm);
        bool includeSpent = false;
        bool filterRecoverableOnly = false;
        bool filterNonRecoverableOnly = false;
        bool? spendableFilter = null; // null = all, true = spendable only, false = non-spendable only

        if (search.ContainsFilter("status"))
        {
            var statusFilters = search.GetFilterArray("status");
            includeSpent = statusFilters.Contains("spent");
            var hasRecoverable = statusFilters.Contains("recoverable");
            var hasUnspent = statusFilters.Contains("unspent");

            // Determine recoverable filtering based on UI selection
            if (hasRecoverable && !hasUnspent)
            {
                filterRecoverableOnly = true;
            }
            else if (hasUnspent && !hasRecoverable)
            {
                filterNonRecoverableOnly = true;
            }
            // If both or neither, show all (no recoverable filter)

            // Check for spendable filter
            var hasSpendable = statusFilters.Contains("spendable");
            var hasNonSpendable = statusFilters.Contains("non-spendable");

            if (hasSpendable && hasNonSpendable)
            {
                // Both selected = show all (no filter)
                spendableFilter = null;
            }
            else if (hasSpendable)
            {
                spendableFilter = true;
            }
            else if (hasNonSpendable)
            {
                spendableFilter = false;
            }
        }
        else
        {
            // Default: show unspent and recoverable
            searchTerm = "status:unspent,status:recoverable";
            search = new SearchString(searchTerm);
        }

        // Get contract scripts for the wallet and fetch VTXOs
        var allContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], cancellationToken: HttpContext.RequestAborted);
        var vtxoContractScripts = allContracts.Select(c => c.Script).ToList();
        var vtxos = await vtxoStorage.GetVtxos(
            scripts: vtxoContractScripts,
            walletIds: [config.WalletId],
            includeSpent: includeSpent,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Apply recoverable filter in-memory if needed
        if (filterRecoverableOnly)
        {
            vtxos = vtxos.Where(v => v.Swept).ToList();
        }
        else if (filterNonRecoverableOnly)
        {
            vtxos = vtxos.Where(v => !v.Swept).ToList();
        }

        // Get spendable coins to determine which VTXOs are actually spendable
        var spendableCoins = await arkadeSpender.GetAvailableCoins(config.WalletId, HttpContext.RequestAborted);
        var spendableOutpoints = spendableCoins
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        // Apply spendable filter if specified
        if (spendableFilter.HasValue)
        {
            vtxos = vtxos
                .Where(vtxo =>
                {
                    var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                    var isSpendable = spendableOutpoints.Contains(outpoint);
                    return spendableFilter.Value ? isSpendable : !isSpendable;
                })
                .ToList();
        }

        // Get contract info for all VTXO scripts
        var vtxoScripts = vtxos.Select(v => v.Script).Distinct().ToArray();
        var vtxoContractsQuery = await contractStorage.GetContracts(
            walletIds: [config.WalletId],
            scripts: vtxoScripts,
            cancellationToken: HttpContext.RequestAborted);
        var vtxoContracts = vtxoContractsQuery.ToDictionary(c => c.Script);

        var model = new StoreVtxosViewModel
        {
            StoreId = storeId,
            Vtxos = vtxos,
            SpendableOutpoints = spendableOutpoints,
            VtxoContracts = vtxoContracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            SearchTerm = searchTerm,
            Search = search
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/intents")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Intents(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreIntentsViewModel { StoreId = storeId });

        // Get state filter using helper
        var stateFilter = ParseEnumFilter<ArkIntentState>(searchTerm, "state", s => s switch
        {
            "waiting-submit" => ArkIntentState.WaitingToSubmit,
            "waiting-batch" => ArkIntentState.WaitingForBatch,
            "batch-succeeded" => ArkIntentState.BatchSucceeded,
            "batch-failed" => ArkIntentState.BatchFailed,
            "cancelled" => ArkIntentState.Cancelled,
            _ => null
        });

        var intents = await intentStorage.GetIntents(
            walletIds: [config.WalletId!],
            states: stateFilter != null ? [stateFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs referenced by intents so the view can show them
        var intentVtxoOutpoints = new Dictionary<string, OutPoint[]>();
        if (intents.Any())
        {
            foreach (var intent in intents)
            {
                if (intent.IntentVtxos.Length > 0)
                    intentVtxoOutpoints[intent.IntentTxId] = intent.IntentVtxos;
            }
        }

        // Fetch full VTXO data for all referenced outpoints
        var allOutpoints = intentVtxoOutpoints.Values.SelectMany(ops => ops).Distinct().ToArray();
        var vtxoLookup = new Dictionary<OutPoint, ArkVtxo>();
        if (allOutpoints.Length > 0)
        {
            var vtxos = await vtxoStorage.GetVtxos(outpoints: allOutpoints, includeSpent: true, cancellationToken: HttpContext.RequestAborted);
            vtxoLookup = vtxos.ToDictionary(v => v.OutPoint);
        }

        return View(new StoreIntentsViewModel
        {
            StoreId = storeId,
            Intents = intents,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            IntentVtxoOutpoints = intentVtxoOutpoints,
            VtxoLookup = vtxoLookup
        });
    }

    [HttpGet("stores/{storeId}/enable-ln")]
    [HttpPost("stores/{storeId}/enable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EnableLightning(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var lightningPaymentMethodId = GetLightningPaymentMethod();
        var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

        store!.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], new LightningPaymentMethodConfig
        {
            ConnectionString = $"type=arkade;wallet-id={config!.WalletId}",
        });
        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lnurlPaymentMethodId], new LNURLPaymentMethodConfig
        {
            UseBech32Scheme = true,
            LUD12Enabled = false
        });

        var blob = store.GetStoreBlob();
        blob.SetExcluded(lightningPaymentMethodId, false);
        blob.OnChainWithLnInvoiceFallback = true;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return RedirectWithSuccess(nameof(StoreOverview), "Lightning enabled", new { storeId });
    }

    [HttpPost("stores/{storeId}/disable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DisableLightning(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        store!.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);
        await storeRepository.UpdateStore(store);
        return RedirectWithSuccess(nameof(StoreOverview), "Lightning disabled", new { storeId });
    }

    [HttpPost("stores/{storeId}/clear-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ClearWallet(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var lnConfig = store!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled = lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;

        store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
        if (lnEnabled)
            store.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);

        await storeRepository.UpdateStore(store);
        return RedirectWithSuccess(nameof(InitialSetup), "Ark wallet configuration cleared.", new { storeId });
    }

    [HttpPost("stores/{storeId}/force-refresh")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ForceRefresh(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var coins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, cancellationToken);
            if (coins.Count == 0)
                return RedirectWithError(nameof(StoreOverview), "No VTXOs available to refresh.", new { storeId });

            var refreshWallet = await walletStorage.GetWalletById(config.WalletId!, cancellationToken);
            if (refreshWallet == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Wallet not found.";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }

            // Get destination for refresh (back to same wallet)
            // Use AwaitingFundsBeforeDeactivate so the contract can be deactivated if the intent fails
            var destination = await contractService.DeriveContract(
                refreshWallet.Id,
                NextContractPurpose.SendToSelf,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                cancellationToken: cancellationToken);
            var totalAmount = coins.Sum(c => c.TxOut.Value);


            // Build ArkIntentSpec for refresh (send back to wallet)
            var arkIntentSpec = new ArkIntentSpec(
                [.. coins],
                [new ArkTxOut(ArkTxOutType.Vtxo, totalAmount, destination.GetArkAddress())],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)
            );

            // Create intent via NNark (automatically cancels any overlapping intents)
            var intentTxId = await intentGenerationService.GenerateManualIntent(
                config.WalletId,
                arkIntentSpec,
                cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = $"Refresh intent {intentTxId} created with {coins.Count} VTXOs. Intent will be submitted automatically.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to create refresh intent: {ex.Message}";
        }

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpPost("stores/{storeId}/cancel-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> CancelIntent(string storeId, string intentTxId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            // Get the intent from storage - filter by wallet to prevent cross-wallet access
            var intents = await intentStorage.GetIntents(
                walletIds: [config!.WalletId],
                intentTxIds: [intentTxId],
                cancellationToken: cancellationToken);
            var intent = intents.FirstOrDefault();
            if (intent == null)
                return RedirectWithError(nameof(Intents), "Intent not found.", new { storeId });

            // If intent was submitted, delete from server
            if (intent.State == ArkIntentState.WaitingForBatch)
            {
                try
                {

                    await clientTransport.DeleteIntent(intent, cancellationToken);
                }
                catch (Exception e)
                {
                    // Log and continue - we will still mark as cancelled in storage even if server deletion fails
                    
                }
            }

            // Update storage to mark as cancelled
            await intentStorage.SaveIntent(intent.WalletId, intent with
            {
                State = NArk.Abstractions.Intents.ArkIntentState.Cancelled,
                CancellationReason = "User requested cancellation",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return RedirectWithSuccess(nameof(Intents), "Intent cancelled successfully.", new { storeId });
        }
        catch (InvalidOperationException ex)
        {
            return RedirectWithError(nameof(Intents), ex.Message, new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Intents), $"Failed to cancel intent: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/sync-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncWallet(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            return RedirectWithSuccess(nameof(StoreOverview), "Wallet synchronized successfully. All contracts and VTXOs have been updated.", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(StoreOverview), $"Failed to sync wallet: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/sync-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            await vtxoSyncService.PollScriptsForVtxos(contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract VTXOs updated successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to sync contract: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/delete-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DeleteContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Only allow deletion if wallet is generated by store
        if (!config!.GeneratedByStore)
            return RedirectWithError(nameof(Contracts), "Cannot delete contract: Wallet is not managed by this store.", new { storeId });

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            // Check if contract has any pending swaps
            var swaps = await swapStorage.GetSwaps(walletIds: [config.WalletId!], contractScripts: [script], status: [ArkSwapStatus.Pending], cancellationToken: cancellationToken);
            if (swaps.Any())
                return RedirectWithError(nameof(Contracts), "Cannot delete contract: It has pending swaps.", new { storeId });

            // Delete the contract (cascade will delete related swaps)
            await contractStorage.DeleteContract(config.WalletId, script, cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract deleted successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to delete contract: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/import-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ImportContract(string storeId, string contractString, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Only allow import if wallet is generated by store
        if (!config!.GeneratedByStore)
            return RedirectWithError(nameof(Contracts), "Cannot import contract: Wallet is not managed by this store.", new { storeId });

        if (string.IsNullOrWhiteSpace(contractString))
            return RedirectWithError(nameof(Contracts), "Contract string is required.", new { storeId });

        try
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

            // Parse the contract string to validate it
            var arkContract = ArkContractParser.Parse(contractString, terms.Network);
            if (arkContract == null)
                return RedirectWithError(nameof(Contracts), "Failed to parse contract. Invalid contract type or data.", new { storeId });

            var script = arkContract.GetArkAddress().ScriptPubKey;
            var scriptHex = script.ToHex();

            // Check if contract already exists
            var existingContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], scripts: [scriptHex], cancellationToken: cancellationToken);
            if (existingContracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract already exists in this wallet.", new { storeId });

            // Create the contract using ToEntity and save via storage
            var contractEntity = arkContract.ToEntity(config.WalletId);
            await contractStorage.SaveContract(contractEntity, cancellationToken);

            // Sync the wallet to detect any VTXOs for this contract
            var allContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(allContracts.Select(c => c.Script).ToHashSet(), cancellationToken);

            return RedirectWithSuccess(nameof(Contracts), $"Contract imported successfully: {arkContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet)}", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to import contract: {ex.Message}", new { storeId });
        }
    }

    private bool IsArkadeLightningEnabled()
    {
        var store = HttpContext.GetStoreData();
        var lnConfig =
            store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled =
            lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        return lnEnabled;
    }

    private async Task<TemporaryWalletSettings> GetFromInputWallet(string? wallet)
    {
        if (string.IsNullOrWhiteSpace(wallet))
            return new TemporaryWalletSettings(GenerateWallet(), null, null, true, true);

        if (wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase))
        {
            return new TemporaryWalletSettings(wallet, null, null, true, false);
        }

        // Check if input is a BIP-39 mnemonic (12 or 24 words)
        var words = wallet.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 12 or 24)
        {
            try
            {
                // Validate the mnemonic
                var mnemonic = new Mnemonic(wallet.Trim(), Wordlist.English);
                return new TemporaryWalletSettings(mnemonic.ToString(), null, null, true, false);
            }
            catch
            {
                // Not a valid mnemonic, continue to other checks
            }
        }

        if (ArkAddress.TryParse(wallet, out var addr))
        {
            var terms = await clientTransport.GetServerInfoAsync();
            var serverKey = terms.SignerKey.Extract().XOnlyPubKey;

            return !serverKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()) ? throw new Exception("Invalid destination address") : new TemporaryWalletSettings(GenerateWallet(), null, wallet, true, true);
        }
        var existingWallet = await walletStorage.GetWalletById(wallet, HttpContext.RequestAborted);
        return existingWallet == null ? throw new Exception("Unsupported value. Enter a BIP-39 seed phrase (12 or 24 words), nsec private key, Ark address, or wallet ID.") : new TemporaryWalletSettings(null, wallet, null, false, false);
    }
    private static string GenerateWallet()
    {
        // Generate HD wallet with BIP-39 mnemonic (12 words)
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        return mnemonic.ToString();
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

    private record TemporaryWalletSettings(string? Wallet, string? WalletId, string? Destination, bool IsOwnedByStore, bool IsNewlyGeneratedWallet);

    [HttpGet("~/stores/{storeId}/payout-processors/ark-automated")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId)
    {
        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery()
                {
                    Stores = new[] { storeId },
                    Processors = new[] { payoutSenderFactory.Processor },
                    PayoutMethods = new[]
                    {
                        ArkadePlugin.ArkadePayoutMethodId
                    }
                }))
            .FirstOrDefault();

        return View(new ConfigureArkPayoutProcessorViewModel(activeProcessor is null ? new ArkAutomatedPayoutBlob() : ArkAutomatedPayoutProcessor.GetBlob(activeProcessor)));
    }
    
    [HttpPost("~/stores/{storeId}/payout-processors/ark-automated/")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId, ConfigureArkPayoutProcessorViewModel automatedTransferBlob)
    {
        if (!ModelState.IsValid)
            return View(automatedTransferBlob);
        
        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery()
                {
                    Stores = [storeId],
                    Processors = [payoutSenderFactory.Processor],
                    PayoutMethods =
                    [
                        ArkadePlugin.ArkadePayoutMethodId
                    ]
                }))
            .FirstOrDefault();
        activeProcessor ??= new PayoutProcessorData();
        activeProcessor.HasTypedBlob<ArkAutomatedPayoutBlob>().SetBlob(automatedTransferBlob.ToBlob());
        activeProcessor.StoreId = storeId;
        activeProcessor.PayoutMethodId = ArkadePlugin.ArkadePayoutMethodId.ToString();
        activeProcessor.Processor = payoutSenderFactory.Processor;
        var tcs = new TaskCompletionSource();
        eventAggregator.Publish(new PayoutProcessorUpdated()
        {
            Data = activeProcessor,
            Id = activeProcessor.Id,
            Processed = tcs
        });
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Processor updated."
        });
        await tcs.Task;
        return RedirectToAction(nameof(ConfigurePayoutProcessor), "Ark", new { storeId });
    }
[NonAction]
    public async Task<ArkBalancesViewModel> GetArkBalances(string walletId, CancellationToken cancellationToken)
    {
        // Get all contract scripts for the wallet
        // var contracts = await contractStorage.GetContracts(walletIds: [walletId], cancellationToken: cancellationToken);
        // var contractScripts = contracts.Select(c => c.Script).ToList();

        // Get unspent VTXOs for those contracts
        // var vtxos = await vtxoStorage.GetVtxos(scripts: contractScripts, cancellationToken: cancellationToken);
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(cancellationToken); 
        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);

        var coinsByRecoverableStatus = allCoins.ToLookup(coin => coin.IsRecoverable(currentTime));
        // var spendableOutpoints = coinsByRecoverableStatus[false].Select(coin => coin.Outpoint).ToHashSet();
        // var recoverableOutpoints = coinsByRecoverableStatus[true].Select(coin => coin.Outpoint).ToHashSet();

        // Available: actually spendable right now (not recoverable, passes contract conditions)
        // var availableBalance = vtxos
        //     .Where(vtxo => spendableOutpoints.Contains(vtxo.OutPoint))
        //     .Sum(vtxo => (long)vtxo.Amount);
        //
        // // Recoverable: spendable but marked as recoverable
        // var recoverableBalance = vtxos
        //     .Where(vtxo => recoverableOutpoints.Contains(vtxo.OutPoint))
        //     .Sum(vtxo => (long)vtxo.Amount);

       

        // Unspendable: unspent VTXOs that don't pass contract conditions yet (e.g., HTLC timelock not reached)
        // These are not recoverable, not locked, but also not spendable
        var allSpendableOutpoints = allCoins
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        var all = (await vtxoStorage
            .GetVtxos(walletIds: [walletId],
                includeSpent: false, cancellationToken: cancellationToken));
        
        var unspendableBalance =
            all.Where(vtxo => !allSpendableOutpoints.Contains(vtxo.OutPoint))
            .Sum(vtxo => (long)vtxo.Amount);

        var availableBalance = coinsByRecoverableStatus[false].Sum(coin => coin.Amount.Satoshi);
        var recoverableBalance = coinsByRecoverableStatus[true].Sum(coin => coin.Amount.Satoshi);

        // Locked: VTXOs committed to active intents (WaitingToSubmit, WaitingForBatch)
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, cancellationToken);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var lockedBalance = coinsByRecoverableStatus[false]
            .Where(coin => lockedSet.Contains(coin.Outpoint))
            .Sum(coin => coin.Amount.Satoshi);

        return new ArkBalancesViewModel
        {
            AvailableBalance = availableBalance - lockedBalance,
            LockedBalance = lockedBalance,
            RecoverableBalance = recoverableBalance,
            UnspendableBalance = unspendableBalance,
        };
    }

    [HttpGet("blockchain-info")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBlockchainInfo(CancellationToken cancellationToken = default)
    {
        try
        {
            var (timestamp, height) = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
            return Json(new { timestamp, height });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("~/ark-admin/wallet/{walletId}")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminWalletOverview(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        // Check if wallet exists
        var adminWallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        if (adminWallet == null)
            return RedirectWithError(nameof(ListWallets), "Wallet not found.");

        var destination = adminWallet.Destination;
        var balances = await GetArkBalances(walletId, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken) != null;

        // Get the default/active contract address
        string? defaultAddress = null;
        var adminActiveContracts = await contractStorage.GetContracts(walletIds: [walletId], isActive: true, take: 1, cancellationToken: cancellationToken);
        var adminActiveContract = adminActiveContracts.FirstOrDefault();
        if (adminActiveContract != null)
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
            var script = Script.FromHex(adminActiveContract.Script);
            var serverKey = OutputDescriptorHelpers.Extract(terms.SignerKey).XOnlyPubKey;
            var address = ArkAddress.FromScriptPubKey(script, serverKey);
            defaultAddress = address.ToString(terms.Network.ChainName == ChainName.Mainnet);
        }

        // Check Ark Operator connection using helper
        var (arkOperatorConnected, arkOperatorError) = await CheckServiceConnectionAsync(
            ct => clientTransport.GetServerInfoAsync(ct), cancellationToken);

        // Check Boltz connection using helper
        var (boltzConnected, boltzError) = boltzClient != null
            ? await CheckServiceConnectionAsync(ct => boltzClient.GetVersionAsync(), cancellationToken)
            : (false, null);

        ViewData["IsAdminView"] = true;
        ViewData["WalletId"] = walletId;

        return View("StoreOverview", new StoreOverviewViewModel
        {
            IsDestinationSweepEnabled = destination is not null,
            IsLightningEnabled = false, // Admin view doesn't check Lightning
            Balances = balances,
            WalletId = walletId,
            Destination = destination,
            SignerAvailable = signerAvailable,
            Wallet = adminWallet.Secret,
            DefaultAddress = defaultAddress,
            ArkOperatorUrl = arkNetworkConfig.ArkUri,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = arkOperatorError,
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError
        });
    }

    [HttpGet("~/ark-admin/wallets")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ListWallets(CancellationToken cancellationToken)
    {
        var wallets = await GetWalletsWithDetailsAsync(cancellationToken);
        return View(wallets);
    }

    [HttpPost("~/ark-admin/wallet/{walletId}/delete")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminDeleteWallet(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        try
        {
            // Check if wallet exists
            var wallet = await GetWalletWithDetailsAsync(walletId, cancellationToken);
            if (wallet == null)
                return RedirectWithError(nameof(ListWallets), "Wallet not found.");

            // Check if wallet has any pending swaps
            var hasPendingSwaps = await HasPendingSwapsAsync(walletId, cancellationToken);
            if (hasPendingSwaps)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending swaps.", new { walletId });

            // Check if wallet has any pending intents
            var hasPendingIntents = await HasPendingIntentsAsync(walletId, cancellationToken);
            if (hasPendingIntents)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending intents.", new { walletId });

            // Delete the wallet and all associated data
            await walletStorage.DeleteWallet(walletId, cancellationToken);
            return RedirectWithSuccess(nameof(ListWallets), $"Wallet {walletId} and all associated data deleted successfully.");
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(AdminWalletOverview), $"Failed to delete wallet: {ex.Message}", new { walletId });
        }
    }

    #region Helper Methods

    /// <summary>
    /// Validates store data and Arkade configuration, returning an error result if validation fails.
    /// </summary>
    private (StoreData? store, ArkadePaymentMethodConfig? config, IActionResult? errorResult)
        ValidateStoreAndConfig(bool requireOwnedByStore = false)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return (null, null, NotFound());

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return (null, null, RedirectToAction(nameof(InitialSetup), new { storeId = store.Id }));

        if (requireOwnedByStore && !config.GeneratedByStore)
            return (null, null, RedirectToAction(nameof(StoreOverview), new { storeId = store.Id }));

        return (store, config, null);
    }

    /// <summary>
    /// Redirects to an action with a success message.
    /// </summary>
    private IActionResult RedirectWithSuccess(string action, string message, object? routeValues = null)
    {
        TempData[WellKnownTempData.SuccessMessage] = message;
        return RedirectToAction(action, routeValues);
    }

    /// <summary>
    /// Redirects to an action with an error message.
    /// </summary>
    private IActionResult RedirectWithError(string action, string message, object? routeValues = null)
    {
        TempData[WellKnownTempData.ErrorMessage] = message;
        return RedirectToAction(action, routeValues);
    }

    /// <summary>
    /// Checks service connection and returns connection status.
    /// </summary>
    private async Task<(bool connected, string? error)> CheckServiceConnectionAsync<T>(
        Func<CancellationToken, Task<T?>> connectionTest,
        CancellationToken ct)
    {
        try
        {
            var result = await connectionTest(ct);
            return (result != null, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Parses an enum filter from search term.
    /// </summary>
    private T? ParseEnumFilter<T>(string? searchTerm, string filterName, Func<string, T?> mapper) where T : struct
    {
        var search = new SearchString(searchTerm);
        if (!search.ContainsFilter(filterName)) return null;
        var filters = search.GetFilterArray(filterName);
        return filters.Length == 1 ? mapper(filters[0]) : null;
    }

    /// <summary>
    /// Parses a boolean filter from search term.
    /// </summary>
    private bool? ParseBooleanFilter(string? searchTerm, string filterName, string trueValue)
    {
        var search = new SearchString(searchTerm);
        if (!search.ContainsFilter(filterName)) return null;
        var filters = search.GetFilterArray(filterName);
        return filters.Length == 1 ? filters[0] == trueValue : null;
    }

    /// <summary>
    /// Gets Boltz connection status and cached limits.
    /// </summary>
    private async Task<(bool connected, string? error, BoltzAllLimits? limits)> GetBoltzConnectionStatusAsync(
        CancellationToken cancellationToken)
    {
        if (boltzLimitsValidator == null)
            return (false, null, null);

        try
        {
            var limits = await boltzLimitsValidator.GetAllLimitsAsync(cancellationToken);
            return (limits != null, limits == null ? "Boltz instance does not support Ark" : null, limits);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    #endregion

    #region Mass Actions

    [HttpPost("stores/{storeId}/vtxos/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionVtxos(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Vtxos), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "build-intent":
                case "build-transaction":
                    // Redirect to new unified Send wizard
                    return RedirectToAction(nameof(Send), new { storeId, vtxos = string.Join(",", selectedItems) });

                case "refresh-state":
                    // Get wallet scripts and poll for updates
                    var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], cancellationToken: cancellationToken);
                    await vtxoSyncService.PollScriptsForVtxos(contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
                    return RedirectWithSuccess(nameof(Vtxos), $"Refreshed state for {selectedItems.Length} VTXOs.", new { storeId });

                default:
                    return RedirectWithError(nameof(Vtxos), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Vtxos), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/swaps/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionSwaps(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Swaps), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "poll-status":
                    if (boltzClient == null)
                        return RedirectWithError(nameof(Swaps), "Boltz client is not configured.", new { storeId });

                    var updatedCount = 0;
                    // Batch fetch all swaps at once for efficiency
                    var swapsToCheck = await swapStorage.GetSwaps(
                        walletIds: [config!.WalletId!],
                        swapIds: selectedItems,
                        cancellationToken: cancellationToken);
                    var swapsDict = swapsToCheck.ToDictionary(s => s.SwapId);

                    foreach (var swapId in selectedItems)
                    {
                        if (!swapsDict.TryGetValue(swapId, out var swap))
                            continue;

                        var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, cancellationToken);
                        var newStatus = MapBoltzStatus(statusResponse.Status);

                        if (swap.Status != newStatus)
                        {
                            await swapStorage.UpdateSwapStatus(config.WalletId!, swapId, newStatus, cancellationToken: cancellationToken);
                            updatedCount++;
                        }
                    }
                    return RedirectWithSuccess(nameof(Swaps), $"Polled {selectedItems.Length} swaps. {updatedCount} status updates.", new { storeId });

                default:
                    return RedirectWithError(nameof(Swaps), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Swaps), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionContracts(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "sync-selected":
                    // Poll all scripts for VTXO updates
                    await vtxoSyncService.PollScriptsForVtxos(selectedItems.ToHashSet(), cancellationToken);
                    return RedirectWithSuccess(nameof(Contracts), $"Synced {selectedItems.Length} contracts.", new { storeId });

                case "set-active":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Active, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Active.", new { storeId });

                case "set-inactive":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Inactive, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Inactive.", new { storeId });

                case "set-awaiting":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Awaiting Funds.", new { storeId });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/vtxos-sublist/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionVtxosSublist(string storeId, string contractScript, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "build-intent":
                    // Redirect to spend/intent builder with selected VTXOs
                    return RedirectToAction(nameof(SpendOverview), new { storeId, vtxoOutpoints = string.Join(",", selectedItems) });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/swaps-sublist/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionSwapsSublist(string storeId, string contractScript, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "poll-status":
                    if (boltzClient == null)
                        return RedirectWithError(nameof(Contracts), "Boltz client is not configured.", new { storeId });

                    var updatedSwapCount = 0;
                    // Batch fetch all swaps at once for efficiency
                    var swapsForContracts = await swapStorage.GetSwaps(
                        walletIds: [config!.WalletId!],
                        swapIds: selectedItems,
                        cancellationToken: cancellationToken);
                    var contractSwapsDict = swapsForContracts.ToDictionary(s => s.SwapId);

                    foreach (var swapId in selectedItems)
                    {
                        if (!contractSwapsDict.TryGetValue(swapId, out var swap))
                            continue;

                        var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, cancellationToken);
                        var newStatus = MapBoltzStatus(statusResponse.Status);

                        if (swap.Status != newStatus)
                        {
                            await swapStorage.UpdateSwapStatus(config.WalletId!, swapId, newStatus, cancellationToken: cancellationToken);
                            updatedSwapCount++;
                        }
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Polled {selectedItems.Length} swaps. {updatedSwapCount} status updates.", new { storeId });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    /// <summary>
    /// Parses outpoint strings (txid:index) into OutPoint objects.
    /// </summary>
    private static HashSet<OutPoint> ParseOutpoints(string[] outpointStrings)
    {
        var outpoints = new HashSet<OutPoint>();
        foreach (var str in outpointStrings)
        {
            var parts = str.Split(':');
            if (parts.Length == 2 && uint256.TryParse(parts[0], out var txid) && uint.TryParse(parts[1], out var index))
            {
                outpoints.Add(new OutPoint(txid, index));
            }
        }
        return outpoints;
    }

    #endregion

    #region Send2 - Simplified Send (No JavaScript)

    /// <summary>
    /// Send2 - Deprecated. Redirects to unified Send wizard.
    /// </summary>
    [HttpGet("stores/{storeId}/send2")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult Send2(
        string storeId,
        string? destinations = null)
    {
        return RedirectToAction(nameof(Send), new { storeId, destinations });
    }

    /// <summary>
    /// Send2 - Add a destination.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/add")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Add(
        string storeId,
        [FromForm] Send2ViewModel model,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        // Parse and add new destination
        if (!string.IsNullOrWhiteSpace(model.NewDestination))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = ParseSend2Destination(model.NewDestination.Trim(), model.NewAmountBtc, serverInfo.Network);

            if (!parsed.IsValid)
            {
                newModel.Errors.Add(parsed.Error ?? "Invalid destination");
            }
            else
            {
                parsed.Index = newModel.Destinations.Count;
                newModel.Destinations.Add(parsed);

                // Estimate fees for all destinations
                await EstimateSend2Fees(newModel, config.WalletId!, token);
            }
        }
        else
        {
            newModel.Errors.Add("Please enter a destination");
        }

        // Serialize state for next round-trip
        newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);

        return View("Send2", newModel);
    }

    /// <summary>
    /// Send2 - Remove a destination by index.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/remove")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Remove(
        string storeId,
        [FromForm] Send2ViewModel model,
        [FromForm] int removeIndex,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        // Remove destination
        if (removeIndex >= 0 && removeIndex < newModel.Destinations.Count)
        {
            newModel.Destinations.RemoveAt(removeIndex);

            // Re-index remaining destinations
            for (int i = 0; i < newModel.Destinations.Count; i++)
            {
                newModel.Destinations[i].Index = i;
            }

            // Re-estimate fees
            if (newModel.Destinations.Count > 0)
            {
                await EstimateSend2Fees(newModel, config.WalletId!, token);
            }
        }

        // Serialize state
        newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);

        return View("Send2", newModel);
    }

    /// <summary>
    /// Send2 - Execute the transaction.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/execute")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Execute(
        string storeId,
        [FromForm] Send2ViewModel model,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        if (newModel.Destinations.Count == 0)
        {
            newModel.Errors.Add("No destinations to send to");
            return View("Send2", newModel);
        }

        // Re-estimate fees to ensure we have current values
        await EstimateSend2Fees(newModel, config.WalletId!, token);

        // Validate we have enough balance
        if (newModel.RemainingSats < 0)
        {
            newModel.Errors.Add($"Insufficient balance. Need {newModel.GrandTotalSats:#,0} sats, have {newModel.AvailableBalanceSats:#,0} sats");
            newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
            return View("Send2", newModel);
        }

        try
        {
            // Check for Lightning destinations
            var lightningDests = newModel.Destinations
                .Where(d => d.Type == Send2DestinationType.LightningInvoice || d.Type == Send2DestinationType.Bip21Lightning)
                .ToList();

            if (lightningDests.Count > 0)
            {
                if (newModel.Destinations.Count > 1)
                {
                    newModel.Errors.Add("Lightning payments can only be sent one at a time");
                    newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
                    return View("Send2", newModel);
                }

                // Execute Lightning payment via ArkadeSpendingService
                var lnDest = lightningDests[0];
                var lnDestination = lnDest.ResolvedAddress ?? lnDest.RawDestination;
                await arkadeSpendingService.Spend(store!, lnDestination, token);

                // Mark payout as paid if this was initiated from payout handler
                if (!string.IsNullOrEmpty(lnDest.PayoutId))
                {
                    await MarkPayoutPaid(lnDest.PayoutId, null, token);
                }

                TempData[WellKnownTempData.SuccessMessage] = $"Lightning payment of {lnDest.AmountSats:#,0} sats initiated";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }

            // Build Ark outputs
            var outputs = new List<ArkTxOut>();
            foreach (var dest in newModel.Destinations)
            {
                if (dest.Type == Send2DestinationType.ArkAddress || dest.Type == Send2DestinationType.Bip21Ark)
                {
                    var arkAddr = ArkAddress.Parse(dest.ResolvedAddress!);
                    outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(dest.AmountSats), arkAddr));
                }
                else
                {
                    newModel.Errors.Add($"Unsupported destination type: {dest.Type}");
                    newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
                    return View("Send2", newModel);
                }
            }

            // Execute Ark spend (auto coin selection)
            if (outputs.Count > 0)
            {
                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    outputs.ToArray(),
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), token);

                // Mark payouts as paid if this was initiated from payout handler
                foreach (var dest in newModel.Destinations.Where(d => !string.IsNullOrEmpty(d.PayoutId)))
                {
                    await MarkPayoutPaid(dest.PayoutId!, txId, token);
                }

                TempData[WellKnownTempData.SuccessMessage] = $"Sent {newModel.TotalSendingSats:#,0} sats to {outputs.Count} destination(s). TxId: {txId}";
            }

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            newModel.Errors.Add($"Transaction failed: {ex.Message}");
            newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
            return View("Send2", newModel);
        }
    }

    private async Task MarkPayoutPaid(string payoutId, uint256? txId, CancellationToken token)
    {
        try
        {
            using var disposable = await arkPayoutHandler.PayoutLocker.LockOrNullAsync(payoutId, 0, token);
            if (disposable is null) return;

            var proof = new ArkPayoutProof
            {
                TransactionId = txId ?? uint256.Zero,
                DetectedInBackground = false
            };
            await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
            {
                PayoutId = payoutId,
                Proof = arkPayoutHandler.SerializeProof(proof)
            });
        }
        catch
        {
            // Best-effort: if marking fails, background detection will catch it
        }
    }

    private static DestinationType? MapSend2TypeToDestinationType(Send2DestinationType type) => type switch
    {
        Send2DestinationType.ArkAddress => DestinationType.ArkAddress,
        Send2DestinationType.Bip21Ark => DestinationType.Bip21Uri,
        Send2DestinationType.Bip21Lightning => DestinationType.Bip21Uri,
        Send2DestinationType.LightningInvoice => DestinationType.LightningInvoice,
        Send2DestinationType.Lnurl => DestinationType.LnurlPay,
        _ => null
    };

    private async Task<Send2ViewModel> BuildSend2ViewModel(string storeId, string walletId, CancellationToken token)
    {
        // Get spendable offchain coins only (not recoverable, not locked by pending intents)
        var currTime = await bitcoinTimeChainProvider.GetChainTime(token);
        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, token);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var spendableCoins = allCoins.Where(c => !c.IsRecoverable(currTime) && !lockedSet.Contains(c.Outpoint)).ToList();

        return new Send2ViewModel
        {
            StoreId = storeId,
            AvailableBalanceSats = spendableCoins.Sum(c => c.TxOut.Value.Satoshi),
            SpendableCoinsCount = spendableCoins.Count,
        };
    }

    private async Task<(LNURLPayRequest? info, string? error)> ResolveLnurlAsync(
        string destination, CancellationToken token)
    {
        Uri lnurl;
        if (destination.IsValidEmail())
            lnurl = LNURL.LNURL.ExtractUriFromInternetIdentifier(destination);
        else
            lnurl = LNURL.LNURL.Parse(destination, out _);

        var httpClient = httpClientFactory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);

        var rawInfo = await LNURL.LNURL.FetchInformation(lnurl, httpClient, linked.Token);
        if (rawInfo is not LNURLPayRequest info)
            return (null, "Not a valid LNURL-pay endpoint");

        return (info, null);
    }

    private async Task<(string? bolt11, string? error)> ResolveLnurlToInvoiceAsync(
        string destination, long amountSats, Network network, CancellationToken token)
    {
        var (info, error) = await ResolveLnurlAsync(destination, token);
        if (info == null) return (null, error ?? "LNURL resolution failed");

        var lm = new LightMoney(amountSats, LightMoneyUnit.Satoshi);
        if (lm < info.MinSendable || lm > info.MaxSendable)
            return (null, $"Amount {amountSats} sats outside LNURL range ({info.MinSendable.ToUnit(LightMoneyUnit.Satoshi)}-{info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi)} sats)");

        var httpClient = httpClientFactory.CreateClient();
        var callback = await info.SendRequest(lm, network, httpClient, cancellationToken: token);
        var bolt11 = callback.GetPaymentRequest(network);
        return (bolt11.ToString(), null);
    }

    private async Task<Send2DestinationViewModel> ParseSend2DestinationAsync(
        string rawDestination, decimal? amountBtc, Network network, CancellationToken token)
    {
        // Check if it's an LNURL or Lightning Address FIRST
        if (rawDestination.IsValidEmail() ||
            rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
        {
            var result = new Send2DestinationViewModel { RawDestination = rawDestination };
            try
            {
                var (info, lnurlError) = await ResolveLnurlAsync(rawDestination, token);
                if (info == null)
                {
                    result.Type = Send2DestinationType.Lnurl;
                    result.Error = lnurlError;
                    return result;
                }

                result.Type = Send2DestinationType.Lnurl;
                result.ResolvedAddress = rawDestination;
                result.LnurlMinSats = (long)info.MinSendable.ToUnit(LightMoneyUnit.Satoshi);
                result.LnurlMaxSats = (long)info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi);

                // Intersect with Boltz submarine swap limits
                if (boltzLimitsValidator != null)
                {
                    var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                    if (limits != null)
                    {
                        result.LnurlMinSats = Math.Max(result.LnurlMinSats, limits.SubmarineMinAmount);
                        result.LnurlMaxSats = Math.Min(result.LnurlMaxSats, limits.SubmarineMaxAmount);
                    }
                }

                var amountSats = amountBtc.HasValue ? (long)(amountBtc.Value * 100_000_000m) : 0L;
                result.AmountSats = amountSats;
                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Type = Send2DestinationType.Lnurl;
                result.Error = $"LNURL resolution failed: {ex.Message}";
                return result;
            }
        }

        // Delegate to existing sync method for all other types
        return ParseSend2Destination(rawDestination, amountBtc, network);
    }

    private static bool IsLightningDestination(string dest) =>
        dest.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
        dest.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase) ||
        dest.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase) ||
        dest.IsValidEmail();

    private Send2DestinationViewModel ParseSend2Destination(string rawDestination, decimal? amountBtc, Network network)
    {
        var result = new Send2DestinationViewModel
        {
            RawDestination = rawDestination
        };

        // Convert amount to sats if provided
        var amountSats = amountBtc.HasValue ? (long)(amountBtc.Value * 100_000_000m) : 0L;

        // Try direct Ark address
        if (ArkAddress.TryParse(rawDestination, out var arkAddress))
        {
            result.Type = Send2DestinationType.ArkAddress;
            result.ResolvedAddress = rawDestination;
            result.AmountSats = amountSats;
            result.IsValid = amountSats > 0;
            if (!result.IsValid)
                result.Error = "Amount is required for Ark address";
            return result;
        }

        // Try BOLT11 Lightning invoice
        if (rawDestination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
            rawDestination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
        {
            var invoiceStr = rawDestination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)
                ? rawDestination[10..]
                : rawDestination;

            try
            {
                var invoice = BOLT11PaymentRequest.Parse(invoiceStr, network);
                result.Type = Send2DestinationType.LightningInvoice;
                result.ResolvedAddress = invoiceStr;
                result.AmountSats = amountSats > 0 ? amountSats : (long)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
                result.IsValid = result.AmountSats > 0;
                if (!result.IsValid)
                    result.Error = "Invoice amount could not be determined";
                return result;
            }
            catch
            {
                result.Error = "Invalid Lightning invoice";
                return result;
            }
        }

        // Try BIP21 URI
        if (Uri.TryCreate(rawDestination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.OrdinalIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            // Extract payout ID if present (from payout handler redirect)
            result.PayoutId = qs["payout"];

            // Extract amount from BIP21 if not provided
            if (amountSats == 0 && qs["amount"] is { } amountStr &&
                decimal.TryParse(amountStr, System.Globalization.CultureInfo.InvariantCulture, out var amountDec))
            {
                amountSats = (long)(amountDec * 100_000_000m);
            }

            // Check for ark= parameter first (preferred)
            if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsArkAddress))
            {
                result.Type = Send2DestinationType.Bip21Ark;
                result.ResolvedAddress = arkQs;
                result.AmountSats = amountSats;
                result.IsValid = amountSats > 0;
                if (!result.IsValid)
                    result.Error = "Amount is required";
                return result;
            }

            // Check for lightning= parameter
            if (qs["lightning"] is { } lnQs)
            {
                try
                {
                    var invoice = BOLT11PaymentRequest.Parse(lnQs, network);
                    result.Type = Send2DestinationType.Bip21Lightning;
                    result.ResolvedAddress = lnQs;
                    result.AmountSats = amountSats > 0 ? amountSats : (long)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
                    result.IsValid = result.AmountSats > 0;
                    if (!result.IsValid)
                        result.Error = "Invoice amount could not be determined";
                    return result;
                }
                catch
                {
                    // Invalid lightning invoice in BIP21
                }
            }

            // Try host as Ark address
            if (ArkAddress.TryParse(host, out var hostArkAddress))
            {
                result.Type = Send2DestinationType.Bip21Ark;
                result.ResolvedAddress = host;
                result.AmountSats = amountSats;
                result.IsValid = amountSats > 0;
                if (!result.IsValid)
                    result.Error = "Amount is required";
                return result;
            }

            // Bitcoin address without ark/lightning is not supported in Send2 (offchain only)
            result.Error = "BIP21 URI does not contain an Ark address or Lightning invoice. Send2 only supports offchain transfers.";
            return result;
        }

        // LNURL / Lightning Address (requires async resolution — use ParseSend2DestinationAsync)
        if (rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase) ||
            rawDestination.IsValidEmail())
        {
            result.Type = Send2DestinationType.Lnurl;
            result.Error = "LNURL/Lightning Address requires async resolution";
            return result;
        }

        result.Error = "Unrecognized destination format. Use an Ark address, Lightning invoice, or BIP21 URI with ark/lightning parameter.";
        return result;
    }

    private async Task EstimateSend2Fees(Send2ViewModel model, string walletId, CancellationToken token)
    {
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(token);
        foreach (var dest in model.Destinations)
        {
            if (!dest.IsValid) continue;

            try
            {
                if (dest.Type == Send2DestinationType.ArkAddress || dest.Type == Send2DestinationType.Bip21Ark)
                {
                    // Ark fee estimation
                    var arkAddr = ArkAddress.Parse(dest.ResolvedAddress!);
                    var outputs = new[] { new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(dest.AmountSats), arkAddr) };

                    var coins = await arkadeSpender.GetAvailableCoins(walletId, token);
                    var feeLockedOps = await intentStorage.GetLockedVtxoOutpoints(walletId, token);
                    var feeLockedSet = new HashSet<NBitcoin.OutPoint>(feeLockedOps);
                    var spendableCoins = coins.Where(c => !c.IsRecoverable(currentTime) && !feeLockedSet.Contains(c.Outpoint)).ToArray();

                    if (spendableCoins.Length > 0)
                    {
                        var fee = await feeEstimator.EstimateFeeAsync(spendableCoins, outputs, token);
                        dest.FeeSats = fee;
                        dest.FeeDescription = "Ark service fee";
                    }
                }
                else if (dest.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning or Send2DestinationType.Lnurl)
                {
                    // Lightning swap fee estimation via Boltz
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                        if (limits != null)
                        {
                            var percentFee = (long)(dest.AmountSats * limits.SubmarineFeePercentage / 100m);
                            var minerFee = limits.SubmarineMinerFee;
                            dest.FeeSats = percentFee + minerFee;
                            dest.FeeDescription = $"Swap fee ({limits.SubmarineFeePercentage:0.##}% + {minerFee:#,0} sat miner)";
                        }
                    }
                }
            }
            catch
            {
                dest.FeeDescription = "Fee estimation unavailable";
            }
        }
    }

    private static string SerializeSend2Destinations(List<Send2DestinationViewModel> destinations)
    {
        // Simple serialization: rawDest|type|resolvedAddr|amountSats|feeSats|isValid|error|payoutId|lnurlMin|lnurlMax;;...
        var parts = destinations.Select(d =>
            $"{d.RawDestination}|{(int)d.Type}|{d.ResolvedAddress ?? ""}|{d.AmountSats}|{d.FeeSats}|{d.IsValid}|{d.Error ?? ""}|{d.PayoutId ?? ""}|{d.LnurlMinSats}|{d.LnurlMaxSats}");
        return string.Join(";;", parts);
    }

    private static void RestoreSend2Destinations(Send2ViewModel model, string? serialized)
    {
        if (string.IsNullOrEmpty(serialized)) return;

        var parts = serialized.Split(";;", StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        foreach (var part in parts)
        {
            var segments = part.Split('|');
            if (segments.Length >= 6)
            {
                model.Destinations.Add(new Send2DestinationViewModel
                {
                    Index = index++,
                    RawDestination = segments[0],
                    Type = Enum.TryParse<Send2DestinationType>(segments[1], out var t) ? t : Send2DestinationType.Unknown,
                    ResolvedAddress = string.IsNullOrEmpty(segments[2]) ? null : segments[2],
                    AmountSats = long.TryParse(segments[3], out var amt) ? amt : 0,
                    FeeSats = long.TryParse(segments[4], out var fee) ? fee : 0,
                    IsValid = bool.TryParse(segments[5], out var valid) && valid,
                    Error = segments.Length > 6 && !string.IsNullOrEmpty(segments[6]) ? segments[6] : null,
                    PayoutId = segments.Length > 7 && !string.IsNullOrEmpty(segments[7]) ? segments[7] : null,
                    LnurlMinSats = segments.Length > 8 && long.TryParse(segments[8], out var lnMin) ? lnMin : 0,
                    LnurlMaxSats = segments.Length > 9 && long.TryParse(segments[9], out var lnMax) ? lnMax : 0,
                });
            }
        }
    }

    /// <summary>
    /// Parses the destinations query parameter which can contain:
    /// - Full BIP21 URIs (comma-separated, may contain colons in scheme)
    /// - Simple format: addr:amount pairs (comma-separated)
    /// </summary>
    private List<Send2DestinationViewModel> ParseDestinationsParam(string destinations, Network network)
    {
        var result = new List<Send2DestinationViewModel>();

        // Smart split: don't split on commas inside BIP21 URIs
        // BIP21 URIs start with "bitcoin:" and may contain query params with commas
        var parts = new List<string>();
        var currentPart = "";
        var inUri = false;

        foreach (var c in destinations)
        {
            if (c == 'b' && currentPart == "" && destinations.IndexOf("bitcoin:", destinations.IndexOf(c.ToString()), StringComparison.OrdinalIgnoreCase) == destinations.IndexOf(c.ToString()))
            {
                inUri = true;
            }

            if (c == ',' && !inUri)
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                continue;
            }

            // End of URI detection (space or next bitcoin:)
            if (inUri && (c == ' ' || (c == ',' && currentPart.Contains('?'))))
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                inUri = c != ',';
                continue;
            }

            currentPart += c;
        }

        if (!string.IsNullOrWhiteSpace(currentPart))
            parts.Add(currentPart.Trim());

        int index = 0;
        foreach (var part in parts)
        {
            // Check if this is a BIP21 URI
            if (part.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            // Check if this is a Lightning invoice
            else if (part.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
                     part.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            // Check if this is an Ark address (no colon, or ark1 prefix)
            else if (part.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
                     ArkAddress.TryParse(part.Split(':')[0], out _))
            {
                // Could be addr:amount format
                var segments = part.Split(':', 2);
                var rawDest = segments[0].Trim();
                decimal? amount = segments.Length > 1 &&
                                  decimal.TryParse(segments[1], System.Globalization.CultureInfo.InvariantCulture, out var amt)
                    ? amt
                    : null;

                var parsed = ParseSend2Destination(rawDest, amount, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            else
            {
                // Unknown format, try to parse anyway
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
        }

        return result;
    }

    #endregion

    #region BTCPay-specific wallet storage helpers

    /// <summary>
    /// BTCPay-specific helper to get all wallets with their related contracts and swaps.
    /// </summary>
    private async Task<List<ArkWalletEntity>> GetWalletsWithDetailsAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to get a wallet with its related contracts and swaps.
    /// </summary>
    private async Task<ArkWalletEntity?> GetWalletWithDetailsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to check if a wallet has pending swaps.
    /// </summary>
    private async Task<bool> HasPendingSwapsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Swaps
            .AnyAsync(s => s.WalletId == walletId &&
                          s.Status == ArkSwapStatus.Pending,
                     cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to check if a wallet has pending intents.
    /// </summary>
    private async Task<bool> HasPendingIntentsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Intents
            .AnyAsync(i => i.WalletId == walletId &&
                          (i.State == ArkIntentState.WaitingToSubmit ||
                           i.State == ArkIntentState.WaitingForBatch ||
                           i.State == ArkIntentState.BatchInProgress),
                     cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to update a wallet's destination address.
    /// </summary>
    private async Task UpdateWalletDestinationAsync(string walletId, string? destination, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var wallet = await ctx.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet != null)
        {
            wallet.WalletDestination = destination;
            await ctx.SaveChangesAsync(cancellationToken);
        }
    }

    #endregion
}

