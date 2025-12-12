
using System.Globalization;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.PayoutProcessors.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NArk;
using NArk.Boltz.Client;
using NArk.Contracts;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController(
    BoltzService? boltzService,
    BoltzClient? boltzClient,
    ArkConfiguration arkConfiguration,
IAuthorizationService authorizationService,
    ArkPayoutHandler arkPayoutHandler,
    ArkPluginDbContextFactory dbContextFactory,
    StoreRepository storeRepository,
    ArkWalletService arkWalletService,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IOperatorTermsService operatorTermsService,
    ArkadeSpendingService arkadeSpendingService,
    ArkAutomatedPayoutSenderFactory payoutSenderFactory,
    PayoutProcessorService payoutProcessorService,
    PullPaymentHostedService pullPaymentHostedService,
    EventAggregator eventAggregator,
    ArkadeWalletSignerProvider walletSignerProvider,
    ArkIntentService arkIntentService,
    ArkadeSpender arkadeSpender,
    BitcoinTimeChainProvider bitcoinTimeChainProvider,
    TrackedContractsCache trackedContractsCache) : Controller
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
                    walletSettings = walletSettings with
                    {
                        WalletId =
                            await arkWalletService.Upsert(
                                walletSettings.Wallet,
                                walletSettings.Destination,
                                walletSettings.IsOwnedByStore,
                                HttpContext.RequestAborted
                            )
                    };
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

            TempData[WellKnownTempData.SuccessMessage] = "Ark Payment method updated.";

            return RedirectToAction(nameof(InitialSetup), new { storeId });
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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction(nameof(InitialSetup), new { storeId = store.Id });

        var destination = await arkWalletService.GetWalletDestination(config.WalletId, cancellationToken);
        
        // Get balances with error handling - indexer service may be unavailable
        ArkBalancesViewModel? balances = null;
        try
        {
            balances = await GetArkBalances(config.WalletId, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log the error but don't fail the entire page
            TempData[WellKnownTempData.ErrorMessage] = $"Unable to fetch balances: {ex.Message}";
        }
        
        var signerAvailable = await walletSignerProvider.GetSigner(config.WalletId, cancellationToken) is not null;
        var includeData = config.GeneratedByStore ||
                          (await authorizationService.AuthorizeAsync(User, null,
                              new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;
        var walletInfo = await arkWalletService.GetWalletInfo(config.WalletId, includeData);
        
        // Get the default/active contract address
        string? defaultAddress = null;
        await using (var dbContext = dbContextFactory.CreateContext())
        {
            var activeContract = await dbContext.WalletContracts
                .Where(c => c.WalletId == config.WalletId && c.Active)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (activeContract != null)
            {
                var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
                var script = Script.FromHex(activeContract.Script);
                var address = ArkAddress.FromScriptPubKey(script, terms.SignerKey);
                defaultAddress = address.ToString(terms.Network.ChainName == ChainName.Mainnet);
            }
        }
        
        // Check Ark Operator connection
        string? arkOperatorUrl = arkConfiguration.ArkUri;
        bool arkOperatorConnected = false;
        string? arkOperatorError = null;
        try
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            arkOperatorConnected = terms != null;
        }
        catch (Exception ex)
        {
            arkOperatorError = ex.Message;
        }
        
        // Check Boltz connection and get cached limits
        string? boltzUrl = arkConfiguration.BoltzUri;
        bool boltzConnected = false;
        string? boltzError = null;
        long? boltzReverseMinAmount = null;
        long? boltzReverseMaxAmount = null;
        decimal? boltzReverseFeePercentage = null;
        long? boltzReverseMinerFee = null;
        long? boltzSubmarineMinAmount = null;
        long? boltzSubmarineMaxAmount = null;
        decimal? boltzSubmarineFeePercentage = null;
        long? boltzSubmarineMinerFee = null;
        
        try
        {
            if (boltzService != null)
            {
                // Get cached limits from BoltzService (fetches if expired)
                var limits = await boltzService.GetLimitsAsync(cancellationToken);
                if (limits != null)
                {
                    boltzConnected = true;
                    boltzReverseMinAmount = limits.ReverseMinAmount;
                    boltzReverseMaxAmount = limits.ReverseMaxAmount;
                    boltzReverseFeePercentage = limits.ReverseFeePercentage;
                    boltzReverseMinerFee = limits.ReverseMinerFee;
                    boltzSubmarineMinAmount = limits.SubmarineMinAmount;
                    boltzSubmarineMaxAmount = limits.SubmarineMaxAmount;
                    boltzSubmarineFeePercentage = limits.SubmarineFeePercentage;
                    boltzSubmarineMinerFee = limits.SubmarineMinerFee;
                }
            }
        }
        catch (Exception ex)
        {
            boltzError = ex.Message;
        }
        
        return View(new StoreOverviewViewModel 
        { 
            IsDestinationSweepEnabled = destination is not null, 
            IsLightningEnabled = IsArkadeLightningEnabled(),
            Balances = balances,
            WalletId = config.WalletId,
            Destination = destination,
            SignerAvailable = signerAvailable,
            Wallet = walletInfo?.Wallet,
            DefaultAddress = defaultAddress,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            ArkOperatorUrl = arkOperatorUrl,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = arkOperatorError,
            BoltzUrl = boltzUrl,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError,
            BoltzReverseMinAmount = boltzReverseMinAmount,
            BoltzReverseMaxAmount = boltzReverseMaxAmount,
            BoltzReverseFeePercentage = boltzReverseFeePercentage,
            BoltzReverseMinerFee = boltzReverseMinerFee,
            BoltzSubmarineMinAmount = boltzSubmarineMinAmount,
            BoltzSubmarineMaxAmount = boltzSubmarineMaxAmount,
            BoltzSubmarineFeePercentage = boltzSubmarineFeePercentage,
            BoltzSubmarineMinerFee = boltzSubmarineMinerFee
        });
    }

    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(string[]? destinations, CancellationToken token)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction(nameof(InitialSetup), new { storeId = store.Id });

        if (!config.GeneratedByStore)
            return RedirectToAction(nameof(StoreOverview), new { storeId = store.Id });

        var balances = await GetArkBalances(config.WalletId, token);

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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return RedirectToAction(nameof(InitialSetup), new { storeId });

        if (command == "clear-destination")
        {
            await arkWalletService.SetWalletDestination(config.WalletId, null, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Auto-sweep destination cleared.";
            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }

        if (command == "save" && !string.IsNullOrEmpty(model.Destination))
        {
            // Prevent setting auto-sweep if sub-dust amounts are enabled
            if (config.AllowSubDustAmounts)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot configure auto-sweep while sub-dust amounts are enabled. Disable sub-dust amounts first.";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }
            
            try
            {
                await arkWalletService.SetWalletDestination(config.WalletId, model.Destination, cancellationToken);
                TempData[WellKnownTempData.SuccessMessage] = "Auto-sweep destination updated.";
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Failed to update destination: {ex.Message}";
            }
            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }

        if (command == "toggle-subdust")
        {
            // Check if auto-sweep is enabled
            var destination = await arkWalletService.GetWalletDestination(config.WalletId, cancellationToken);
            
            // Prevent enabling sub-dust when auto-sweep is configured
            if (!config.AllowSubDustAmounts && !string.IsNullOrEmpty(destination))
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot enable sub-dust amounts while auto-sweep is configured. Clear the auto-sweep destination first.";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }
            
            var newConfig = config with { AllowSubDustAmounts = !config.AllowSubDustAmounts };
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
            await storeRepository.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = newConfig.AllowSubDustAmounts 
                ? "Sub-dust amounts enabled for Arkade payments." 
                : "Sub-dust amounts disabled for Arkade payments.";
            return RedirectToAction(nameof(StoreOverview), new { storeId });
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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        if (!config.GeneratedByStore)
            return View(new StoreContractsViewModel { StoreId = storeId });

        // Get status filter
        bool? activeFilter = null;
        if (new SearchString(searchTerm).ContainsFilter("status"))
        {
            var statusFilters = new SearchString(searchTerm).GetFilterArray("status");
            if (statusFilters.Length == 1)
            {
                activeFilter = statusFilters[0] == "active";
            }
        }

        // Always load VTXOs and include all (spent and recoverable)
        var (contracts, contractVtxos) = await arkWalletService.GetArkWalletContractsAsync(
            config.WalletId, 
            skip, 
            count, 
            searchText ?? "", 
            activeFilter,
            includeVtxos: true,
            allowSpent: true,
            allowNote: true,
            HttpContext.RequestAborted);

        // Always load swaps
        var contractSwaps = new Dictionary<string, ArkSwap[]>();
        if (contracts.Any())
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var contractScripts = contracts.Select(c => c.Script).ToHashSet();
            
            var swaps = await dbContext.Swaps
                .Where(s => s.WalletId == config.WalletId && contractScripts.Contains(s.ContractScript))
                .OrderByDescending(s => s.CreatedAt)
                .ToArrayAsync(HttpContext.RequestAborted);
            
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
            CachedSwapScripts = boltzService?.GetActiveSwapsCache().Values.ToHashSet() ?? [],
            CachedContractScripts = trackedContractsCache.Contracts.Select(c => c.Script).ToHashSet()
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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        if (!config.GeneratedByStore)
            return View(new StoreSwapsViewModel { StoreId = storeId });

        // Get status filter
        ArkSwapStatus? statusFilter = null;
        if (new SearchString(searchTerm).ContainsFilter("status"))
        {
            var statusFilters = new SearchString(searchTerm).GetFilterArray("status");
            if (statusFilters.Length == 1)
            {
                statusFilter = statusFilters[0] switch
                {
                    "pending" => ArkSwapStatus.Pending,
                    "settled" => ArkSwapStatus.Settled,
                    "failed" => ArkSwapStatus.Failed,
                    _ => null
                };
            }
        }

        // Get type filter
        ArkSwapType? typeFilter = null;
        if (new SearchString(searchTerm).ContainsFilter("type"))
        {
            var typeFilters = new SearchString(searchTerm).GetFilterArray("type");
            if (typeFilters.Length == 1)
            {
                typeFilter = typeFilters[0] switch
                {
                    "reverse" => ArkSwapType.ReverseSubmarine,
                    "submarine" => ArkSwapType.Submarine,
                    _ => null
                };
            }
        }

        var swaps = await arkWalletService.GetArkWalletSwapsAsync(
            config.WalletId,
            skip,
            count,
            searchText ?? "",
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
            CachedSwapIds = boltzService?.GetActiveSwapsCache().Keys.ToHashSet() ?? []
        };

        return View(model);
    }

    [HttpPost("stores/{storeId}/swaps/{swapId}/poll")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PollSwap(string storeId, string swapId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return NotFound();

        try
        {
            if (boltzService == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Boltz service is not configured";
                return RedirectToAction(nameof(Swaps), new { storeId });
            }
            
            // Poll the specific swap
            var (updates, matchedScripts) = await boltzService.PollActiveManually(
                swaps => swaps.Where(swap => swap.SwapId == swapId && swap.WalletId == config.WalletId),
                HttpContext.RequestAborted);

            if (updates.Count > 0)
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Swap {swapId} polled successfully. Status: {updates[0].Swap.Status}";
            }
            else if (matchedScripts.Count > 0)
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Swap {swapId} polled successfully. No status change detected.";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Swap {swapId} not found or not active.";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Error polling swap: {ex.Message}";
        }

        return RedirectToAction("Swaps", new { storeId });
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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        if (!config.GeneratedByStore)
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

        var vtxos = await arkWalletService.GetArkWalletVtxosAsync(
            config.WalletId,
            skip,
            count,
            searchText ?? "",
            includeSpent,
            includeRecoverable,
            HttpContext.RequestAborted);

        // Get spendable coins to determine which VTXOs are actually spendable
        var spendableCoins = await arkadeSpender.GetSpendableCoins([config.WalletId], true, HttpContext.RequestAborted);
        var spendableOutpoints = spendableCoins
            .SelectMany(kvp => kvp.Value)
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
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        if (!config.GeneratedByStore)
            return View(new StoreIntentsViewModel { StoreId = storeId });

        // Get state filter
        ArkIntentState? stateFilter = null;
        if (new SearchString(searchTerm).ContainsFilter("state"))
        {
            var stateFilters = new SearchString(searchTerm).GetFilterArray("state");
            if (stateFilters.Length == 1)
            {
                stateFilter = stateFilters[0] switch
                {
                    "waiting-submit" => ArkIntentState.WaitingToSubmit,
                    "waiting-batch" => ArkIntentState.WaitingForBatch,
                    "batch-succeeded" => ArkIntentState.BatchSucceeded,
                    "batch-failed" => ArkIntentState.BatchFailed,
                    "cancelled" => ArkIntentState.Cancelled,
                    _ => null
                };
            }
        }

        var intents = await arkWalletService.GetArkWalletIntentsAsync(
            config.WalletId,
            skip,
            count,
            searchText ?? "",
            stateFilter,
            HttpContext.RequestAborted);

        // Always load VTXOs
        var intentVtxos = new Dictionary<int, ArkIntentVtxo[]>();
        if (intents.Any())
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var intentIds = intents.Select(i => i.InternalId).ToHashSet();
            
            var vtxos = await dbContext.IntentVtxos
                .Include(iv => iv.Vtxo)
                .Where(iv => intentIds.Contains(iv.InternalId))
                .ToArrayAsync(HttpContext.RequestAborted);
            
            intentVtxos = vtxos
                .GroupBy(iv => iv.InternalId)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        var model = new StoreIntentsViewModel
        {
            StoreId = storeId,
            Intents = intents,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            IntentVtxos = intentVtxos
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/enable-ln")]
    [HttpPost("stores/{storeId}/enable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EnableLightning(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        var lightningPaymentMethodId = GetLightningPaymentMethod();
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
        await storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "Lightning enabled";
        return RedirectToAction("StoreOverview", new { storeId });
    }

    [HttpPost("stores/{storeId}/disable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DisableLightning(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        // Remove Lightning payment method configuration
        store.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);
        await storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "Lightning disabled";
        return RedirectToAction("StoreOverview", new { storeId });
    }

    [HttpPost("stores/{storeId}/clear-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ClearWallet(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        // Check if Lightning is enabled via Arkade
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled = lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;

        // Remove Ark payment method configuration
        store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
        
        // Remove Lightning if it was enabled via Arkade
        if (lnEnabled)
        {
            store.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);
        }

        await storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "Ark wallet configuration cleared.";
        return RedirectToAction("InitialSetup", new { storeId });
    }

    [HttpPost("stores/{storeId}/force-refresh")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ForceRefresh(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        try
        {
            // Get all spendable coins including recoverable ones
            var coinSets = await arkadeSpender.GetSpendableCoins([config.WalletId], true, cancellationToken);
            
            if (!coinSets.TryGetValue(config.WalletId, out var coins) || coins.Count == 0)
            {
                TempData[WellKnownTempData.ErrorMessage] = "No VTXOs available to refresh.";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }

            // Create intent with all VTXOs (no outputs = refresh intent)
            var intentId = await arkIntentService.CreateIntentAsync(
                config.WalletId,
                coins.ToArray(),
                null,
                null,
                null,
                cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = $"Refresh intent created with {coins.Count} VTXOs. Intent will be submitted automatically.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to create refresh intent: {ex.Message}";
        }

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpPost("stores/{storeId}/cancel-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> CancelIntent(string storeId, string internalId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        try
        {
            await arkIntentService.CancelIntentAsync(internalId, "User requested cancellation", cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Intent cancelled successfully.";
        }
        catch (InvalidOperationException ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex.Message;
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to cancel intent: {ex.Message}";
        }

        return RedirectToAction(nameof(Intents), new { storeId });
    }

    [HttpPost("stores/{storeId}/sync-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncWallet(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        try
        {
            await arkWalletService.UpdateBalances(config.WalletId, false, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Wallet synchronized successfully. All contracts and VTXOs have been updated.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to sync wallet: {ex.Message}";
        }

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpPost("stores/{storeId}/sync-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var contract = await dbContext.WalletContracts
                .FirstOrDefaultAsync(c => c.WalletId == config.WalletId && c.Script == script, cancellationToken);

            if (contract == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Contract not found.";
                return RedirectToAction(nameof(Contracts), new { storeId });
            }

            await arkWalletService.UpdateBalances(config.WalletId, false, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "Contract VTXOs updated successfully.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to sync contract: {ex.Message}";
        }

        return RedirectToAction(nameof(Contracts), new { storeId });
    }

    [HttpPost("stores/{storeId}/delete-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DeleteContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        // Only allow deletion if wallet is generated by store
        if (!config.GeneratedByStore)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Cannot delete contract: Wallet is not managed by this store.";
            return RedirectToAction(nameof(Contracts), new { storeId });
        }

        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var contract = await dbContext.WalletContracts
                .Include(c => c.Swaps)
                .FirstOrDefaultAsync(c => c.WalletId == config.WalletId && c.Script == script, cancellationToken);

            if (contract == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Contract not found.";
                return RedirectToAction(nameof(Contracts), new { storeId });
            }
            //
            // // Check if contract has any unspent VTXOs
            // var hasUnspentVtxos = await dbContext.Vtxos
            //     .AnyAsync(v => v.Script == script && (v.SpentByTransactionId == null || v.SpentByTransactionId == ""), cancellationToken);
            //
            // if (hasUnspentVtxos)
            // {
            //     TempData[WellKnownTempData.ErrorMessage] = "Cannot delete contract: It has unspent VTXOs. Please spend them first.";
            //     return RedirectToAction(nameof(Contracts), new { storeId });
            // }

            // Check if contract has any pending swaps
            var hasPendingSwaps = contract.Swaps?.Any(s => s.Status == ArkSwapStatus.Pending) ?? false;
            if (hasPendingSwaps)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot delete contract: It has pending swaps.";
                return RedirectToAction(nameof(Contracts), new { storeId });
            }

            // Delete the contract (cascade will delete related swaps)
            dbContext.WalletContracts.Remove(contract);
            await dbContext.SaveChangesAsync(cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = "Contract deleted successfully.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to delete contract: {ex.Message}";
        }

        return RedirectToAction(nameof(Contracts), new { storeId });
    }

    [HttpPost("stores/{storeId}/import-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ImportContract(string storeId, string contractString, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        // Only allow import if wallet is generated by store
        if (!config.GeneratedByStore)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Cannot import contract: Wallet is not managed by this store.";
            return RedirectToAction(nameof(Contracts), new { storeId });
        }

        if (string.IsNullOrWhiteSpace(contractString))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Contract string is required.";
            return RedirectToAction(nameof(Contracts), new { storeId });
        }

        try
        {
            // Parse the contract string to extract type and data
            // Try to parse the contract to validate it
            var arkContract = ArkContract.Parse(contractString);
            if (arkContract == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Failed to parse contract. Invalid contract type or data.";
                return RedirectToAction(nameof(Contracts), new { storeId });
            }

            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            var script = arkContract.GetArkAddress().ScriptPubKey;
            var scriptHex = script.ToHex();

            await using var dbContext = dbContextFactory.CreateContext();
            
            // Check if contract already exists
            var existingContract = await dbContext.WalletContracts
                .FirstOrDefaultAsync(c => c.WalletId == config.WalletId && c.Script == scriptHex, cancellationToken);

            if (existingContract != null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Contract already exists in this wallet.";
                return RedirectToAction(nameof(Contracts), new { storeId });
            }

            // Create the contract
            var newContract = new ArkWalletContract
            {
                Script = scriptHex,
                WalletId = config.WalletId,
                Type = arkContract.Type,
                ContractData = arkContract.GetContractData(),
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            dbContext.WalletContracts.Add(newContract);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Sync the wallet to detect any VTXOs for this contract
            await arkWalletService.UpdateBalances(config.WalletId, false, cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = $"Contract imported successfully: {arkContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet)}";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to import contract: {ex.Message}";
        }

        return RedirectToAction(nameof(Contracts), new { storeId });
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
            return new TemporaryWalletSettings(GenerateWallet(), null, null, true);

        if (wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase))
        {
            return new TemporaryWalletSettings(wallet, null, null, true);
        }

        if (ArkAddress.TryParse(wallet, out var addr))
        {
            var terms = await operatorTermsService.GetOperatorTerms();

            if (!terms.SignerKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
                throw new Exception("Invalid destination address");

            return new TemporaryWalletSettings(GenerateWallet(), null, wallet, true);
        }

        if (HexEncoder.IsWellFormed(wallet) &&
            Encoders.Hex.DecodeData(wallet) is
            { Length: 32 } potentialWalletBytes &&
            ECXOnlyPubKey.TryCreate(potentialWalletBytes, out _))
        {
            if (!await arkWalletService.WalletExists(wallet, HttpContext.RequestAborted))
                throw new Exception("Unsupported value.");

            return new TemporaryWalletSettings(null, wallet, null, false);
        }

        throw new Exception("Unsupported value.");
    }
    private static string GenerateWallet()
    {
        var key = RandomUtils.GetBytes(32)!;
        var encoder = Encoders.Bech32("nsec");
        encoder.SquashBytes = true;
        encoder.StrictLength = false;
        var nsec = encoder.EncodeData(key, Bech32EncodingType.BECH32);
        return nsec;
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

    private record TemporaryWalletSettings(string? Wallet, string? WalletId, string? Destination, bool IsOwnedByStore);

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
        await using var dbContext = dbContextFactory.CreateContext();

        // Get all VTXOs for the wallet
        var contracts = await dbContext.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        var vtxos = await dbContext.Vtxos
            .Where(vtxo => contracts.Contains(vtxo.Script))
            .Where(vtxo => (vtxo.SpentByTransactionId == null || vtxo.SpentByTransactionId == ""))
            .ToListAsync(cancellationToken);

        // Get actually spendable coins using ArkadeSpender logic
        var spendableCoins = await arkadeSpender.GetSpendableCoins([walletId], false, cancellationToken);
        var spendableOutpoints = spendableCoins
            .SelectMany(kvp => kvp.Value)
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        // Get spendable coins including recoverable
        var spendableCoinsWithRecoverable = await arkadeSpender.GetSpendableCoins([walletId], true, cancellationToken);
        var recoverableOutpoints = spendableCoinsWithRecoverable
            .SelectMany(kvp => kvp.Value)
            .Where(coin => coin.Recoverable)
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        // Available: actually spendable right now (not recoverable, passes contract conditions)
        var availableBalance = vtxos
            .Where(vtxo => spendableOutpoints.Contains(new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex)))
            .Sum(vtxo => vtxo.Amount);

        // Recoverable: spendable but marked as recoverable
        var recoverableBalance = vtxos
            .Where(vtxo => recoverableOutpoints.Contains(new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex)))
            .Sum(vtxo => vtxo.Amount);

        // Locked: VTXOs that are linked to pending intents
        var intentVtxoScripts = await dbContext.IntentVtxos
            .Include(iv => iv.Intent)
            .Include(iv => iv.Vtxo)
            .Where(iv => iv.Intent.WalletId == walletId && 
                        (iv.Intent.State == ArkIntentState.WaitingToSubmit || 
                         iv.Intent.State == ArkIntentState.WaitingForBatch))
            .Select(iv => new { iv.Vtxo.TransactionId, iv.Vtxo.TransactionOutputIndex })
            .ToListAsync(cancellationToken);

        var lockedBalance = vtxos
            .Where(vtxo => intentVtxoScripts.Any(iv => 
                iv.TransactionId == vtxo.TransactionId && 
                iv.TransactionOutputIndex == vtxo.TransactionOutputIndex))
            .Sum(vtxo => vtxo.Amount);

        // Unspendable: unspent VTXOs that don't pass contract conditions yet (e.g., HTLC timelock not reached)
        // These are not recoverable, not locked, but also not spendable
        var allSpendableOutpoints = spendableCoins
            .SelectMany(kvp => kvp.Value)
            .Select(coin => coin.Outpoint)
            .Concat(recoverableOutpoints)
            .ToHashSet();

        var unspendableBalance = vtxos
            .Where(vtxo => 
            {
                var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                var isLocked = intentVtxoScripts.Any(iv => 
                    iv.TransactionId == vtxo.TransactionId && 
                    iv.TransactionOutputIndex == vtxo.TransactionOutputIndex);
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
            var (timestamp, height) = await bitcoinTimeChainProvider.Get(cancellationToken);
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
        if (!await arkWalletService.WalletExists(walletId, cancellationToken))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Wallet not found.";
            return RedirectToAction("ListWallets");
        }

        var destination = await arkWalletService.GetWalletDestination(walletId, cancellationToken);
        var balances = await GetArkBalances(walletId, cancellationToken);
        var signerAvailable = await walletSignerProvider.GetSigner(walletId, cancellationToken) is not null;
        var walletInfo = await arkWalletService.GetWalletInfo(walletId, true);
        
        // Get the default/active contract address
        string? defaultAddress = null;
        await using (var dbContext = dbContextFactory.CreateContext())
        {
            var activeContract = await dbContext.WalletContracts
                .Where(c => c.WalletId == walletId && c.Active)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (activeContract != null)
            {
                var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
                var script = Script.FromHex(activeContract.Script);
                var address = ArkAddress.FromScriptPubKey(script, terms.SignerKey);
                defaultAddress = address.ToString(terms.Network.ChainName == ChainName.Mainnet);
            }
        }
        
        // Check Ark Operator connection
        string? arkOperatorUrl = arkConfiguration.ArkUri;
        bool arkOperatorConnected = false;
        string? arkOperatorError = null;
        try
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            arkOperatorConnected = terms != null;
        }
        catch (Exception ex)
        {
            arkOperatorError = ex.Message;
        }
        
        // Check Boltz connection
        string? boltzUrl = arkConfiguration.BoltzUri;
        bool boltzConnected = false;
        string? boltzError = null;
        try
        {
            var pairs = boltzClient != null ? await boltzClient.GetVersionAsync() : null;
            boltzConnected = pairs != null;
        }
        catch (Exception ex)
        {
            boltzError = ex.Message;
        }
        
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
            Wallet = walletInfo?.Wallet,
            DefaultAddress = defaultAddress,
            ArkOperatorUrl = arkOperatorUrl,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = arkOperatorError,
            BoltzUrl = boltzUrl,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError
        });
    }

    [HttpGet("~/ark-admin/wallets")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ListWallets(CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var wallets = await dbContext.Wallets
            .Include(wallet => wallet.Contracts)
            .Include(wallet => wallet.Swaps)
            .ToListAsync(cancellationToken);

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
            await using var dbContext = dbContextFactory.CreateContext();
            
            var wallet = await dbContext.Wallets
                .Include(w => w.Contracts)
                .Include(w => w.Swaps)
                .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);

            if (wallet == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Wallet not found.";
                return RedirectToAction(nameof(ListWallets));
            }

            // Check if wallet has any unspent VTXOs
            var contractScripts = wallet.Contracts.Select(c => c.Script).ToList();
            var hasUnspentVtxos = await dbContext.Vtxos
                .AnyAsync(v => contractScripts.Contains(v.Script) && 
                              (v.SpentByTransactionId == null || v.SpentByTransactionId == ""), 
                         cancellationToken);
            
            // Check if wallet has any pending swaps
            var hasPendingSwaps = wallet.Swaps?.Any(s => s.Status == ArkSwapStatus.Pending) ?? false;
            if (hasPendingSwaps)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot delete wallet: It has pending swaps.";
                return RedirectToAction(nameof(AdminWalletOverview), new { walletId });
            }

            // Check if wallet has any pending intents
            var hasPendingIntents = await dbContext.Intents
                .AnyAsync(i => i.WalletId == walletId && 
                              (i.State == ArkIntentState.WaitingToSubmit || 
                               i.State == ArkIntentState.WaitingForBatch), 
                         cancellationToken);

            if (hasPendingIntents)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot delete wallet: It has pending intents.";
                return RedirectToAction(nameof(AdminWalletOverview), new { walletId });
            }

            // Delete all VTXOs associated with the wallet's contracts
            var vtxos = await dbContext.Vtxos
                .Where(v => contractScripts.Contains(v.Script))
                .ToListAsync(cancellationToken);
            dbContext.Vtxos.RemoveRange(vtxos);

            // Delete all intents and their associated data
            var intents = await dbContext.Intents
                .Include(i => i.IntentVtxos)
                .Where(i => i.WalletId == walletId)
                .ToListAsync(cancellationToken);
            dbContext.Intents.RemoveRange(intents);

            // Delete the wallet (cascade will delete contracts and swaps)
            dbContext.Wallets.Remove(wallet);
            
            await dbContext.SaveChangesAsync(cancellationToken);

            TempData[WellKnownTempData.SuccessMessage] = $"Wallet {walletId} and all associated data deleted successfully.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to delete wallet: {ex.Message}";
            return RedirectToAction(nameof(AdminWalletOverview), new { walletId });
        }

        return RedirectToAction(nameof(ListWallets));
    }
}

