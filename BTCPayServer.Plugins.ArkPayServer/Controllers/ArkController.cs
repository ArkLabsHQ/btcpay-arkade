using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Helpers;
using NArk.Core.Contracts;
using NArk.Hosting;
using NArk.Core.Services;
using NArk.Core.Transport;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController(
    BoltzLimitsService? boltzLimitsService,
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
    IContractService contractService,
    IChainTimeProvider bitcoinTimeChainProvider,
    VtxoPollingService vtxoPollingService,
    EfCoreContractStorage contractStorage,
    EfCoreSwapStorage swapStorage,
    EfCoreVtxoStorage vtxoStorage,
    EfCoreIntentStorage efCoreIntentStorage,
    EfCoreWalletStorage walletStorage) : Controller
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
                    await walletStorage.UpsertWalletAsync(wallet, walletSettings.IsOwnedByStore, HttpContext.RequestAborted);
                    if (wallet.WalletType == WalletType.SingleKey)
                    {
                        var addressProvider = await walletProvider.GetAddressProviderAsync(wallet.Id, HttpContext.RequestAborted);
                        if (addressProvider != null)
                            await addressProvider.GetNextContract(NextContractPurpose.SendToSelf,
                                ContractActivityState.Active, HttpContext.RequestAborted);
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
            if (walletSettings.IsNewlyGeneratedWallet && walletSettings.Wallet != null)
            {
                return RedirectToAction("RecoverySeedBackup", "UIHome", new
                {
                    Mnemonic = walletSettings.Wallet,
                    ReturnUrl = Url.Action(nameof(StoreOverview), new { storeId }),
                    IsStored = true,
                    RequireConfirm = true,
                    CryptoCode = "ARK"
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

        var wallet = await walletStorage.GetWalletByIdAsync(config!.WalletId!, cancellationToken);
        var destination = wallet?.WalletDestination;

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
        var activeContract = await contractStorage.GetFirstActiveContractAsync(config.WalletId!, cancellationToken);
        if (activeContract != null)
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
            var script = Script.FromHex(activeContract.Script);
            var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
            var address = ArkAddress.FromScriptPubKey(script, serverKey);
            defaultAddress = address.ToString(terms.Network.ChainName == ChainName.Mainnet);
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

        return View(new StoreOverviewViewModel
        {
            IsDestinationSweepEnabled = destination is not null,
            IsLightningEnabled = IsArkadeLightningEnabled(),
            Balances = balances,
            WalletId = config.WalletId,
            Destination = destination,
            SignerAvailable = signerAvailable,
            DefaultAddress = defaultAddress,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            Wallet = wallet?.Wallet,
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
            BoltzSubmarineMinerFee = boltzLimits?.SubmarineMinerFee
        });
    }

    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(string[]? destinations, CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return errorResult;

        var balances = await GetArkBalances(config!.WalletId!, token);

        return View(new SpendOverviewViewModel
        {
            PrefilledDestination = destinations?.ToList() ?? [],
            Balances = balances
        });
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

    [HttpPost("stores/{storeId}/update-wallet-config")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> UpdateWalletConfig(string storeId, StoreOverviewViewModel model, string? command = null, CancellationToken cancellationToken = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (command == "clear-destination")
        {
            await walletStorage.UpdateWalletDestinationAsync(config!.WalletId!, null, cancellationToken);
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
                await walletStorage.UpdateWalletDestinationAsync(config.WalletId!, model.Destination, cancellationToken);
                return RedirectWithSuccess(nameof(StoreOverview), "Auto-sweep destination updated.", new { storeId });
            }
            catch (Exception ex)
            {
                return RedirectWithError(nameof(StoreOverview), $"Failed to update destination: {ex.Message}", new { storeId });
            }
        }

        if (command == "toggle-subdust")
        {
            var toggleWallet = await walletStorage.GetWalletByIdAsync(config!.WalletId!, cancellationToken);
            var destination = toggleWallet?.WalletDestination;

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
        var contracts = await contractStorage.GetContractsWithPaginationAsync(
            config.WalletId,
            skip,
            count,
            searchText,
            activeFilter,
            includeSwaps: false,
            HttpContext.RequestAborted);

        // Get VTXOs for the contracts (include spent and recoverable for full history)
        var contractVtxos = new Dictionary<string, Data.Entities.VTXO[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToList();
            var vtxos = await vtxoStorage.GetVtxosByScriptsAndOutpointsAsync(
                contractScripts,
                vtxoOutpoints: null,
                includeSpent: true,
                includeRecoverable: true,
                HttpContext.RequestAborted);

            contractVtxos = vtxos
                .GroupBy(v => v.Script)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        // Always load swaps
        var contractSwaps = new Dictionary<string, Data.Entities.ArkSwap[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script);
            contractSwaps = await swapStorage.GetSwapsByContractScriptsAsync(
                config.WalletId,
                contractScripts,
                HttpContext.RequestAborted);
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
            CachedContractScripts = (await contractStorage.LoadActiveContracts(cancellationToken: HttpContext.RequestAborted))
                .Select(c => c.Script).ToHashSet()
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

        var swaps = await swapStorage.GetSwapsWithPaginationAsync(
            config.WalletId!,
            skip,
            count,
            searchText,
            statusFilter,
            typeFilter,
            HttpContext.RequestAborted);

        var model = new StoreSwapsViewModel
        {
            StoreId = storeId,
            Swaps = swaps,
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

            var swap = await swapStorage.GetSwapByIdAsync(config!.WalletId!, swapId, HttpContext.RequestAborted);
            if (swap == null)
                return RedirectWithError(nameof(Swaps), $"Swap {swapId} not found.", new { storeId });

            var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, HttpContext.RequestAborted);
            var newStatus = MapBoltzStatus(statusResponse.Status);

            if (swap.Status != newStatus)
            {
                await swapStorage.UpdateSwapStatusAsync(config.WalletId!, swapId, newStatus, HttpContext.RequestAborted);
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
        bool includeRecoverable = false;
        bool? spendableFilter = null; // null = all, true = spendable only, false = non-spendable only
        
        if (search.ContainsFilter("status"))
        {
            var statusFilters = search.GetFilterArray("status");
            includeSpent = statusFilters.Contains("spent");
            includeRecoverable = statusFilters.Contains("recoverable");
            
            // If only "unspent" is selected, don't include spent or recoverable
            if (statusFilters.Contains("unspent") && !includeSpent && !includeRecoverable)
            {
                includeSpent = false;
                includeRecoverable = false;
            }
            
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
            // Default: show unspent and recoverable, exclude spent
            includeRecoverable = true;
            searchTerm = "status:unspent,status:recoverable";
            search = new SearchString(searchTerm);
        }

        // Get contract scripts for the wallet and fetch VTXOs
        var vtxoContractScripts = await contractStorage.GetContractScriptsAsync(config.WalletId, HttpContext.RequestAborted);
        var vtxos = await vtxoStorage.GetVtxosWithPaginationAsync(
            vtxoContractScripts,
            skip,
            count,
            searchText,
            includeSpent,
            includeRecoverable,
            HttpContext.RequestAborted);

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

        var model = new StoreVtxosViewModel
        {
            StoreId = storeId,
            Vtxos = vtxos,
            SpendableOutpoints = spendableOutpoints,
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

        var intents = await efCoreIntentStorage.GetIntentsWithPaginationAsync(
            config.WalletId!,
            skip,
            count,
            searchText,
            stateFilter,
            HttpContext.RequestAborted);

        var intentVtxos = new Dictionary<string, ArkIntentVtxo[]>();
        if (intents.Any())
        {
            var intentTxIds = intents.Select(i => i.IntentTxId);
            intentVtxos = await efCoreIntentStorage.GetIntentVtxosByIntentTxIdsAsync(intentTxIds, HttpContext.RequestAborted);
        }

        return View(new StoreIntentsViewModel
        {
            StoreId = storeId,
            Intents = intents,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            IntentVtxos = intentVtxos
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

            var refreshWallet = await walletStorage.GetWalletByIdAsync(config.WalletId!, cancellationToken);
            if (refreshWallet == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Wallet not found.";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }

            // Get destination for refresh (back to same wallet)
            var destination = await contractService.DeriveContract(refreshWallet.Id, NextContractPurpose.SendToSelf, cancellationToken: cancellationToken);
            var totalAmount = coins.Sum(c => c.TxOut.Value);


            // Build ArkIntentSpec for refresh (send back to wallet)
            var arkIntentSpec = new ArkIntentSpec(
                [.. coins],
                [new ArkTxOut(ArkTxOutType.Vtxo, totalAmount, destination.GetArkAddress())],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)
            );

            // Create intent via NNark
            var intentId = await intentGenerationService.GenerateManualIntent(
                config.WalletId,
                arkIntentSpec,
                force: true,
                cancellationToken);                                                            

            TempData[WellKnownTempData.SuccessMessage] = $"Refresh intent {intentId} created with {coins.Count} VTXOs. Intent will be submitted automatically.";
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
            // Get the intent from storage
            var intent = await intentStorage.GetIntentByIntentTxId(intentTxId, cancellationToken);
            if (intent == null)
                return RedirectWithError(nameof(Intents), "Intent not found.", new { storeId });

            // If intent was submitted, delete from server
            if (intent.State == NArk.Abstractions.Intents.ArkIntentState.WaitingForBatch)
            {
                await clientTransport.DeleteIntent(intent, cancellationToken);
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
            var scripts = await contractStorage.GetContractScriptsAsync(config!.WalletId, cancellationToken);
            await vtxoPollingService.PollScriptsForVtxos(scripts.ToHashSet(), cancellationToken);
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
            var contractExists = await contractStorage.ContractExistsAsync(config!.WalletId, script, cancellationToken);
            if (!contractExists)
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            var scripts = await contractStorage.GetContractScriptsAsync(config.WalletId, cancellationToken);
            await vtxoPollingService.PollScriptsForVtxos(scripts.ToHashSet(), cancellationToken);
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
            var contract = await contractStorage.GetContractWithSwapsAsync(config.WalletId, script, cancellationToken);
            if (contract == null)
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            // Check if contract has any pending swaps
            var hasPendingSwaps = contract.Swaps?.Any(s => s.Status == ArkSwapStatus.Pending) ?? false;
            if (hasPendingSwaps)
                return RedirectWithError(nameof(Contracts), "Cannot delete contract: It has pending swaps.", new { storeId });

            // Delete the contract (cascade will delete related swaps)
            await contractStorage.DeleteContractAsync(config.WalletId, script, cancellationToken);
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
            var contractExists = await contractStorage.ContractExistsAsync(config.WalletId, scriptHex, cancellationToken);
            if (contractExists)
                return RedirectWithError(nameof(Contracts), "Contract already exists in this wallet.", new { storeId });

            // Create the contract using ToEntity and save via storage
            var contractEntity = arkContract.ToEntity(config.WalletId);
            await contractStorage.SaveContract( contractEntity, cancellationToken);

            // Sync the wallet to detect any VTXOs for this contract
            var allScripts = await contractStorage.GetContractScriptsAsync(config.WalletId, cancellationToken);
            await vtxoPollingService.PollScriptsForVtxos(allScripts.ToHashSet(), cancellationToken);

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
        var words = wallet.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
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
            var serverKey = OutputDescriptorHelpers.Extract(terms.SignerKey).XOnlyPubKey;

            if (!serverKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
                throw new Exception("Invalid destination address");

            return new TemporaryWalletSettings(GenerateWallet(), null, wallet, true, true);
        }
        var existingWallet = await walletStorage.GetWalletByIdAsync(wallet, HttpContext.RequestAborted);
        if (existingWallet == null)
            throw new Exception("Unsupported value. Enter a BIP-39 seed phrase (12 or 24 words), nsec private key, Ark address, or wallet ID.");

        return new TemporaryWalletSettings(null, wallet, null, false, false);

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

    private async Task<ArkBalancesViewModel> GetArkBalances(string walletId, CancellationToken cancellationToken)
    {
        // Get all contract scripts for the wallet
        var contractScripts = await contractStorage.GetContractScriptsAsync(walletId, cancellationToken);

        // Get unspent VTXOs for those contracts
        var vtxos = await vtxoStorage.GetUnspentVtxosByContractScriptsAsync(contractScripts, cancellationToken);

        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);

        var coinsByRecoverableStatus = allCoins.ToLookup(coin => coin.IsRecoverable());
        var spendableOutpoints = coinsByRecoverableStatus[false].Select(coin => coin.Outpoint).ToHashSet();
        var recoverableOutpoints = coinsByRecoverableStatus[true].Select(coin => coin.Outpoint).ToHashSet();
        
        // Available: actually spendable right now (not recoverable, passes contract conditions)
        var availableBalance = vtxos
            .Where(vtxo => spendableOutpoints.Contains(new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex)))
            .Sum(vtxo => vtxo.Amount);

        // Recoverable: spendable but marked as recoverable
        var recoverableBalance = vtxos
            .Where(vtxo => recoverableOutpoints.Contains(new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex)))
            .Sum(vtxo => vtxo.Amount);

        // Locked: VTXOs that are linked to pending intents
        var lockedVtxoOutpoints = await efCoreIntentStorage.GetLockedVtxoOutpointsAsync(walletId, cancellationToken);

        var lockedBalance = vtxos
            .Where(vtxo => lockedVtxoOutpoints.Any(iv =>
                iv.TransactionId == vtxo.TransactionId &&
                iv.OutputIndex == vtxo.TransactionOutputIndex))
            .Sum(vtxo => vtxo.Amount);

        // Unspendable: unspent VTXOs that don't pass contract conditions yet (e.g., HTLC timelock not reached)
        // These are not recoverable, not locked, but also not spendable
        var allSpendableOutpoints = allCoins
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        var unspendableBalance = vtxos
            .Where(vtxo =>
            {
                var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                var isLocked = lockedVtxoOutpoints.Any(iv =>
                    iv.TransactionId == vtxo.TransactionId &&
                    iv.OutputIndex == vtxo.TransactionOutputIndex);
                return !allSpendableOutpoints.Contains(outpoint) && !isLocked;
            })
            .Sum(vtxo => vtxo.Amount);

        return new ArkBalancesViewModel
        {
            AvailableBalance = availableBalance,
            RecoverableBalance = recoverableBalance,
            UnspendableBalance = unspendableBalance,
            LockedBalance = lockedBalance
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
        var adminWallet = await walletStorage.GetWalletByIdAsync(walletId, cancellationToken);
        if (adminWallet == null)
            return RedirectWithError(nameof(ListWallets), "Wallet not found.");

        var destination = adminWallet.WalletDestination;
        var balances = await GetArkBalances(walletId, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken) != null;

        // Get the default/active contract address
        string? defaultAddress = null;
        var adminActiveContract = await contractStorage.GetFirstActiveContractAsync(walletId, cancellationToken);
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
            Wallet = adminWallet.Wallet,
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
        var wallets = await walletStorage.GetWalletsWithDetailsAsync(cancellationToken);
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
            var wallet = await walletStorage.GetWalletWithDetailsAsync(walletId, cancellationToken);
            if (wallet == null)
                return RedirectWithError(nameof(ListWallets), "Wallet not found.");

            // Check if wallet has any pending swaps
            var hasPendingSwaps = await walletStorage.HasPendingSwapsAsync(walletId, cancellationToken);
            if (hasPendingSwaps)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending swaps.", new { walletId });

            // Check if wallet has any pending intents
            var hasPendingIntents = await walletStorage.HasPendingIntentsAsync(walletId, cancellationToken);
            if (hasPendingIntents)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending intents.", new { walletId });

            // Delete the wallet and all associated data
            await walletStorage.DeleteWalletAsync(walletId, cancellationToken);
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
    private async Task<(bool connected, string? error, BoltzLimitsCache? limits)> GetBoltzConnectionStatusAsync(
        CancellationToken cancellationToken)
    {
        if (boltzLimitsService == null)
            return (false, null, null);

        try
        {
            var limits = await boltzLimitsService.GetLimitsAsync(cancellationToken);
            return (true, null, limits);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    #endregion
}

