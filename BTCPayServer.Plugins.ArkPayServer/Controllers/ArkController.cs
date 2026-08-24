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
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Hosting;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents;
using NArk.Swaps.Models;
using NArk.Core.Wallet;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public partial class ArkController(
    BoltzClient? boltzClient,
    ArkadeSolverService arkadeSolver,
    IArkadeIntentStorage? arkadeIntentStorage,
    ArkNetworkConfig arkNetworkConfig,
    ArkLightningSpendKeyService spendKeyService,
    IAuthorizationService authorizationService,
    ArkPayoutHandler arkPayoutHandler,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IClientTransport clientTransport,
    ArkOperatorHealthService arkOperatorHealth,
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
    NArk.Core.Recovery.ISingleKeyDefaultEnsurer singleKeyDefaultEnsurer,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    IWalletStorage walletStorage,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    BoardingUtxoSyncService boardingUtxoSyncService,
    IWalletLogStore walletLogStore,
    RecoveryStatusTracker recoveryStatusTracker,
    IServiceProvider serviceProvider,
    ILogger<ArkController> logger) : Controller
{
    // Post-operation VTXO refresh only needs to catch updates since the operation
    // started. A 5-minute buffer absorbs clock skew and batch-round latency while
    // keeping the arkd indexer query bounded for wallets with lots of history.
    private static readonly TimeSpan PostOpVtxoPollBuffer = TimeSpan.FromMinutes(5);
    private static DateTimeOffset PostOpVtxoPollSince() => DateTimeOffset.UtcNow - PostOpVtxoPollBuffer;

    /// <summary>
    /// Returns the wallet's Lightning connection string, including its spend capability, so
    /// the owner can add the same wallet to another store they control.
    ///
    /// Gated on <c>requireOwnedByStore</c>: only a store with spend rights over the wallet
    /// may read the capability.
    /// </summary>
    [HttpGet("stores/{storeId}/ln-connection-string")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> LightningConnectionString(string storeId)
    {
        var (_, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return errorResult;

        return Ok(new
        {
            connectionString = await spendKeyService.BuildConnectionStringAsync(
                config!.WalletId, HttpContext.RequestAborted)
        });
    }

    /// <summary>
    /// Issues a fresh spend capability for the wallet. Connection strings previously shared
    /// with other stores stop authorising spends and must be re-copied.
    /// </summary>
    [HttpPost("stores/{storeId}/regenerate-ln-spend-key")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> RegenerateLightningSpendKey(string storeId)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return errorResult;

        await spendKeyService.RegenerateAsync(config!.WalletId, HttpContext.RequestAborted);

        // Re-issue this store's own connection string so it keeps working with the new value.
        var lightningPaymentMethodId = GetLightningPaymentMethod();
        var lnConfig = store!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lightningPaymentMethodId, paymentMethodHandlerDictionary);
        if (lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true)
        {
            lnConfig.ConnectionString = await spendKeyService.BuildConnectionStringAsync(
                config.WalletId, HttpContext.RequestAborted);
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
            await storeRepository.UpdateStore(store);
        }

        return RedirectWithSuccess(nameof(StoreOverview),
            "Spend key regenerated. Connection strings shared with other stores must be updated.",
            new { storeId });
    }

    /// <summary>
    /// Starts unified wallet recovery for <paramref name="walletId"/> on a background
    /// thread (a gap-limit scan polls arkd per index), tracking status for the overview.
    /// Discovers contracts (incl. legacy deprecated-signer scripts) + the derivation
    /// index, restores swaps, finalizes pending txs and resyncs offchain funds, then
    /// syncs boarding (on-chain) UTXOs. <c>IWalletRecoveryService</c> is only registered
    /// when swaps (Boltz) are configured; without it this degrades to a boarding-only sync.
    /// </summary>
    private void StartBackgroundRecovery(string walletId)
    {
        var recoveryService = serviceProvider.GetService<NArk.Swaps.Recovery.IWalletRecoveryService>();
        _ = Task.Run(async () =>
        {
            try
            {
                recoveryStatusTracker.SetRunning(walletId);

                var contractsRecovered = 0;
                var swapsAudited = 0;
                var fundsSynced = 0;
                if (recoveryService is not null)
                {
                    var report = await recoveryService.RecoverAsync(walletId, cancellationToken: CancellationToken.None);
                    contractsRecovered = report.ContractsRecovered;
                    swapsAudited = report.SwapAudit.Count;
                    fundsSynced = report.FundsScriptsSynced;
                }

                // Boarding (on-chain) UTXOs aren't covered by offchain recovery.
                var boardingContracts = (await contractStorage.GetContracts(
                        walletIds: [walletId], scope: ContractScope.Onchain,
                        cancellationToken: CancellationToken.None)).ToList();
                if (boardingContracts.Count > 0)
                    await boardingUtxoSyncService.SyncAsync(boardingContracts, CancellationToken.None);

                recoveryStatusTracker.SetCompleted(walletId,
                    recoveryService is not null ? contractsRecovered : boardingContracts.Count,
                    swapsAudited, fundsSynced);
            }
            catch (Exception ex)
            {
                recoveryStatusTracker.SetFailed(walletId, ex.Message);
                logger.LogWarning(ex, "Background wallet recovery failed for wallet {WalletId}", walletId);
            }
        });
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

    private async Task<TemporaryWalletSettings> GetFromInputWallet(string? wallet, WalletSetupMode mode = WalletSetupMode.Auto)
    {
        // Watch-only path: the input is an account descriptor — a bare
        // tr(pubkey) for single-key style or a tr([fp/path]xpub/0/*) for
        // hierarchical-deterministic style. The merchant does NOT own the
        // signing material (it's on a paired BTCPayApp device or elsewhere),
        // so IsOwnedByStore is false. If the descriptor matches an existing
        // wallet id we reuse it; otherwise we hand the descriptor back to
        // the POST handler with IsWatchOnlyDescriptor=true so it routes to
        // WalletFactory.CreateWatchOnlyWallet rather than CreateWallet.
        if (mode == WalletSetupMode.WatchOnly)
        {
            if (string.IsNullOrWhiteSpace(wallet))
                throw new Exception("Account descriptor is required for watch-only import.");

            var trimmed = wallet.Trim();
            var existingWatchOnly = await walletStorage.GetWalletById(trimmed, HttpContext.RequestAborted);
            if (existingWatchOnly is not null)
                return new TemporaryWalletSettings(null, trimmed, null, false, false);

            return new TemporaryWalletSettings(trimmed, null, null, false, false, IsWatchOnlyDescriptor: true);
        }

        if (string.IsNullOrWhiteSpace(wallet))
            return new TemporaryWalletSettings(GenerateWallet(), null, null, true, true);

        if (wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase))
        {
            // Check all possible wallet ID formats: tr(compressed), raw compressed, raw xonly, tr(xonly).
            // If we find a match, the user is re-importing a wallet that already exists in storage —
            // IsOwnedByStore is still true because they proved ownership by presenting the nsec.
            var candidateIds = new[] { WalletFactory.GetOutputDescriptorFromNsec(wallet) }
                .Concat(WalletFactory.GetAlternateWalletIdsFromNsec(wallet));
            foreach (var candidateId in candidateIds)
            {
                var existing = await walletStorage.GetWalletById(candidateId, HttpContext.RequestAborted);
                if (existing is not null)
                    return new TemporaryWalletSettings(null, candidateId, null, true, false);
            }
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
        return existingWallet == null ? throw new Exception("Unsupported value. Enter a BIP-39 seed phrase (12 or 24 words), nsec private key, Arkade address, or wallet ID.") : new TemporaryWalletSettings(null, wallet, null, false, false);
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

    private record TemporaryWalletSettings(string? Wallet, string? WalletId, string? Destination, bool IsOwnedByStore, bool IsNewlyGeneratedWallet, bool IsWatchOnlyDescriptor = false);

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



    /// <summary>
    /// Checks whether the given wallet ID is referenced by any store's Ark or LN payment method config.
    /// </summary>
    private async Task<bool> IsWalletUsedByAnyStore(string walletId, string? excludeStoreId = null)
    {
        var allStores = await storeRepository.GetStores();
        var lnPaymentMethod = GetLightningPaymentMethod();
        var lnWalletRef = $"wallet-id={walletId}";
        foreach (var s in allStores)
        {
            if (excludeStoreId != null && s.Id == excludeStoreId)
                continue;

            var arkConfig = s.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId, paymentMethodHandlerDictionary);
            if (arkConfig?.WalletId == walletId)
                return true;

            var lnConfig = s.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                lnPaymentMethod, paymentMethodHandlerDictionary);
            if (lnConfig?.ConnectionString?.Contains(lnWalletRef, StringComparison.OrdinalIgnoreCase) is true)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Validates store data and Arkade configuration, returning an error result if validation fails.
    /// Server admins bypass the <paramref name="requireOwnedByStore"/> check.
    /// </summary>
    private async Task<(StoreData? store, ArkadePaymentMethodConfig? config, IActionResult? errorResult)>
        ValidateStoreAndConfig(bool requireOwnedByStore = false)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return (null, null, NotFound());

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return (null, null, RedirectToAction(nameof(InitialSetup), new { storeId = store.Id }));

        if (requireOwnedByStore && !config.GeneratedByStore)
        {
            var isServerAdmin = (await authorizationService.AuthorizeAsync(User, null,
                new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;
            if (!isServerAdmin)
                return (null, null, RedirectToAction(nameof(StoreOverview), new { storeId = store.Id }));
        }

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
    /// Maps an exception to a user-facing message. When the Arkade operator is unreachable
    /// it returns the friendly <see cref="ArkOperatorAvailability.UnavailableMessage"/> and
    /// flips the status banner immediately (so the next page already reflects the outage);
    /// otherwise it returns the original error prefixed with <paramref name="context"/>.
    /// </summary>
    private string DescribeArkError(Exception ex, string context)
    {
        arkOperatorHealth.ReportFailure(ex); // no-op unless ex looks like operator-unreachable
        return ArkOperatorAvailability.Describe(ex, context);
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

}
