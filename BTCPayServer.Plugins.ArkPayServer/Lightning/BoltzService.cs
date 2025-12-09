using System.Collections.Concurrent;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Boltz.Client;
using NArk.Boltz.Models.WebSocket;
using NArk.Contracts;
using NArk.Models;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class BoltzService(
    ArkadeSpender arkadeSpender,
    EventAggregator eventAggregator,
    ArkPluginDbContextFactory dbContextFactory,
    BoltzSwapService boltzSwapService,
    BoltzClient boltzClient,
    ArkWalletService walletService,
    ArkVtxoSynchronizationService arkVtxoSynchronizationService,
    IOperatorTermsService operatorTermsService,
    ILogger<BoltzService> logger) : IHostedService
{
    private CompositeDisposable _leases = new();
    private BoltzWebsocketClient? _wsClient;
    private CancellationTokenSource? _periodicPollCts;
    private async Task<Network> Network(CancellationToken cancellationToken) => (await operatorTermsService.GetOperatorTerms(cancellationToken)).Network;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _leases.Add(eventAggregator.SubscribeAsync<ArkSwapUpdated>(OnLightningSwapUpdated));
        _leases.Add(eventAggregator.SubscribeAsync<VTXOsUpdated>(VTXOSUpdated));
        
        _periodicPollCts = new CancellationTokenSource();
        _ = ListenForSwapUpdates(_periodicPollCts.Token);
        _ = PeriodicSwapPolling(_periodicPollCts.Token);
        
        return Task.CompletedTask;
    }

    private async Task VTXOSUpdated(VTXOsUpdated arg)
    {
       var scripts = arg.Vtxos.Select(vtxo => vtxo.Script).ToArray();
       await PollActiveManually(swaps => swaps.Where(swap => scripts.Contains(swap.ContractScript)), CancellationToken.None);
    }

    private async Task OnLightningSwapUpdated(ArkSwapUpdated arg)
    {
        if (arg.Swap.Status.IsActive())
        {
            if (_activeSwaps.TryAdd(arg.Swap.SwapId, arg.Swap.ContractScript))
            {
                logger.LogInformation("Subscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
               await  _wsClient.SubscribeAsync([arg.Swap.SwapId]);
            }
        }
        else
        {
            if(_activeSwaps.TryRemove(arg.Swap.SwapId, out _))
            {
                logger.LogInformation("Unsubscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
                await  _wsClient.UnsubscribeAsync([arg.Swap.SwapId]);
            }
            
            // Trigger contract sync when swap completes to detect VTXOs
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("Triggering contract sync for swap {SwapId} with script {Script}", 
                        arg.Swap.SwapId, arg.Swap.ContractScript);
                    await arkVtxoSynchronizationService.PollScriptsForVtxos(new HashSet<string>([arg.Swap.ContractScript]), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error syncing contract after swap {SwapId} status update", arg.Swap.SwapId);
                }
            });
        }
        
    }

    private async Task<object> ListenForSwapUpdates(CancellationToken cancellationToken)
    {
        var error = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            Uri? wsUrl = null;
            try
            {
                if(error == "")
                    logger.LogInformation("Start listening for swap updates.");
                wsUrl = boltzClient.DeriveWebSocketUri();
                _wsClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsUrl, cancellationToken);
                error = "";
                logger.LogInformation("Listening for swap updates at {wsUrl}", wsUrl);
                _wsClient.OnAnyEventReceived += OnWebSocketEvent;
                await _wsClient.SubscribeAsync(_activeSwaps.Keys.ToArray(), cancellationToken);
                
                await PollActiveManually(null, cancellationToken);
                await _wsClient.WaitUntilDisconnected(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                var newError = $"Error  listening for swap updates at {wsUrl}";
                if (error != newError)
                {
                    error = newError;
                    logger.LogError(e, error); ;
                }

                try
                {
                    await PollActiveManually(null, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Error polling active swaps as failsafe");
                }
                await Task.Delay(5000, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    private Task OnWebSocketEvent(WebSocketResponse? response)
    {
        try
        {
            if (response is null)
                return Task.CompletedTask;
            
            if (response.Event == "update" && response is {Channel: "swap.update", Args.Count: > 0})
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    _ = HandleSwapUpdate(id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing WebSocket event {@response}", response);
        }
 
        return Task.CompletedTask;
    }
    
    private readonly ConcurrentDictionary<string,string> _activeSwaps = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    
    // Cached Boltz limits
    private BoltzLimitsCache? _limitsCache;
    private readonly SemaphoreSlim _limitsCacheLock = new(1, 1);
    private static readonly TimeSpan LimitsCacheExpiry = TimeSpan.FromMinutes(15);

    public IReadOnlyDictionary<string, string> GetActiveSwapsCache() => _activeSwaps;

    public async Task<(List<ArkSwapUpdated> Updates, HashSet<string> MatchedScripts)> PollActiveManually(Func<IQueryable<ArkSwap>, IQueryable<ArkSwap>>? query = null, CancellationToken cancellationToken = default)
    {
        await _pollLock.WaitAsync(cancellationToken);
        
        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var queryable = dbContext.Swaps
                .Include(swap => swap.Contract)
                .Where(swap => swap.Status == ArkSwapStatus.Pending || swap.Status == ArkSwapStatus.Unknown);

            if (query is not null)
                queryable = query(queryable);
            
            var activeSwaps = await queryable.ToArrayAsync(cancellationToken);
            if (activeSwaps.Length == 0)
            {
                return ([], []);
            }
            
            // Collect all matched contract scripts
            var matchedScripts = activeSwaps.Select(s => s.ContractScript).ToHashSet();
            
            var evts = new List<ArkSwapUpdated>();
            foreach (var swap in activeSwaps)
            {
                var evt = await PollSwapStatus(swap, cancellationToken);
                if (evt != null)
                {
                    evts.Add(evt);
                }
                
            }
            
            await dbContext.SaveChangesAsync(cancellationToken);
            
            // Update cache: add active swaps, remove inactive ones
            foreach (var evt in evts)
            {
                if (evt.Swap.Status.IsActive())
                {
                    _activeSwaps.TryAdd(evt.Swap.SwapId, evt.Swap.ContractScript);
                }
                else
                {
                    _activeSwaps.TryRemove(evt.Swap.SwapId, out _);
                }
            }
            PublishUpdates(evts.ToArray());
            return (evts, matchedScripts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling active swaps");
        }
        finally
        {
            _pollLock.Release();
        }

        return ([], []);
    }

    private void PublishUpdates(params ArkSwapUpdated[] updates)
    {
        var sb = new StringBuilder();
        foreach (var update in updates)
        {
            sb.AppendLine(update.ToString());
            eventAggregator.Publish(update);
        }
        logger.LogInformation(sb.ToString());
    }

    private async Task<ArkSwapUpdated?> PollSwapStatus(ArkSwap swap, CancellationToken cancellationToken)
    {
        try
        {
            var response = await boltzClient.GetSwapStatusAsync(swap.SwapId, cancellationToken);
            var oldStatus = swap.Status;
            if (Map(response.Status) is var newStatus && newStatus != oldStatus)
            {
                swap.UpdatedAt = DateTimeOffset.UtcNow;
                swap.Status = newStatus;
                return new ArkSwapUpdated { Swap = swap };
            }
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Swap not found on Boltz - mark as unknown
            logger.LogWarning("Swap {SwapId} not found on Boltz server", swap.SwapId);
            var oldStatus = swap.Status;
            if (oldStatus != ArkSwapStatus.Unknown)
            {
                swap.UpdatedAt = DateTimeOffset.UtcNow;
                swap.Status = ArkSwapStatus.Unknown;
                return new ArkSwapUpdated { Swap = swap };
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling swap status for {SwapId}", swap.SwapId);
            return null;
        }
    }

    public ArkSwapStatus Map(string status)
    {
        switch (status)
        {
            case "swap.created":
                return ArkSwapStatus.Pending;
            case "invoice.expired":
            case "swap.expired":
            case "transaction.failed":
            case "transaction.refunded":
                return ArkSwapStatus.Failed;
            case "transaction.mempool":
                return ArkSwapStatus.Pending;
            case "transaction.confirmed":
            case "invoice.settled":
                return ArkSwapStatus.Settled;
            default:
                logger.LogInformation("Unknown status {Status}", status);
                return ArkSwapStatus.Unknown;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _periodicPollCts?.Cancel();
        _periodicPollCts?.Dispose();
        _leases.Dispose();
        _leases = new CompositeDisposable();
        _pollLock.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleSwapUpdate(string swapId)
    {
        logger.LogInformation("Received swap update for {SwapId}", swapId);
        var (updates, matchedScripts) =
            await PollActiveManually(swaps => swaps.Where(swap => swap.SwapId == swapId), CancellationToken.None);

        // Always sync VTXOs when we receive a WebSocket update, even if status didn't change
        // The swap may have progressed (e.g., invoice paid, funds received) without changing our status mapping
        if (matchedScripts.Count > 0)
        {
            if (updates.Count == 0)
            {
                logger.LogInformation("No status change for swap {SwapId}, but syncing {Count} contract(s) anyway", 
                    swapId, matchedScripts.Count);
            }
            
            await arkVtxoSynchronizationService.PollScriptsForVtxos(matchedScripts, CancellationToken.None);
        }
    }

    /// <summary>
    /// Periodic polling failsafe to ensure no swaps are missed due to WebSocket issues or race conditions.
    /// Polls all pending swaps every 5 minutes.
    /// </summary>
    private async Task PeriodicSwapPolling(CancellationToken cancellationToken)
    {
        // Wait 30 seconds before starting periodic polling to allow initial setup
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        
        logger.LogInformation("Starting periodic swap polling failsafe (every 5 minutes)");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                
                logger.LogDebug("Running periodic swap poll failsafe");
                var (updates, matchedScripts) = await PollActiveManually(null, cancellationToken);
                
                if (updates.Count > 0)
                {
                    logger.LogInformation("Periodic poll detected {Count} swap status changes", updates.Count);
                    
                    // Sync VTXOs for any completed swaps
                    var completedScripts = updates
                        .Where(u => !u.Swap.Status.IsActive())
                        .Select(u => u.Swap.ContractScript)
                        .ToHashSet();
                    
                    if (completedScripts.Count > 0)
                    {
                        await arkVtxoSynchronizationService.PollScriptsForVtxos(completedScripts, cancellationToken);
                    }
                }
                else if (matchedScripts.Count > 0)
                {
                    // No status changes but we have pending swaps - sync them anyway
                    logger.LogDebug("Periodic poll: no status changes, but syncing {Count} pending swap contract(s)", matchedScripts.Count);
                    await arkVtxoSynchronizationService.PollScriptsForVtxos(matchedScripts, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in periodic swap polling failsafe");
                // Continue polling despite errors
            }
        }
        
        logger.LogInformation("Periodic swap polling stopped");
    }

    public async Task<ArkSwap> CreateReverseSwap(string walletId, CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellationToken)
    {
        // Validate amount against Boltz limits
        var amountSats = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var (isValid, errorMessage) = await ValidateAmountAsync(amountSats, isReverse: true, cancellationToken);
        if (!isValid)
        {
            throw new InvalidOperationException(errorMessage);
        }

        if (!await walletService.CanHandle(walletId, cancellationToken))
        {
             throw new InvalidOperationException("No signer found for wallet");
        }

        var signer =await  walletService.CreateSigner(walletId, cancellationToken);

        await using var dbContext = dbContextFactory.CreateContext();
        
        // Get the wallet from the database to extract the receiver key
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet == null)
        {
            throw new InvalidOperationException($"Wallet with ID {walletId} not found");
        }
        
        var network = await Network(cancellationToken);
        

        ReverseSwapResult? swapResult = null;
        ArkWalletContract? arkWalletContract = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var receiverKey = await signer.GetPublicKey(cancellationToken);

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateReverseSwap(
                createInvoiceRequest,
                receiverKey, cancellationToken);
            
            try
            {
                // Parse the invoice to get the actual invoice amount (which includes fees)
                var bolt11 = BOLT11PaymentRequest.Parse(swapResult.Swap.Invoice, network);
                var invoiceAmountSats = (long)bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
                
                // Validate fees: user pays invoiceAmountSats, merchant receives onchainAmount
                var (isValid, errorMessage) = await ValidateFeesAsync(invoiceAmountSats, amountSats, isReverse: true, cancellationToken);
                if (!isValid)
                {
                    throw new InvalidOperationException(errorMessage);
                }
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                // Log but don't fail if we can't validate fees (e.g., network issues)
                logger.LogWarning(e, "Unable to validate reverse swap fees, proceeding anyway");
            }
            
            var contractScript = swapResult.Contract.GetArkAddress().ScriptPubKey.ToHex();
            arkWalletContract =new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.Contract.Type,
                Active = true,
                ContractData = swapResult.Contract.GetContractData()
            };
                return (arkWalletContract, swapResult.Contract);
        }, cancellationToken);

        if (swapResult is null || arkWalletContract is null || contract is not VHTLCContract htlcContract) 
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex(); 
        dbContext.ChangeTracker.TrackGraph(arkWalletContract, node => node.Entry.State = EntityState.Unchanged);

        var reverseSwap = new ArkSwap
        {
            SwapId = swapResult.Swap.Id,
            WalletId = walletId,
            SwapType =  ArkSwapType.ReverseSubmarine,
            Invoice = swapResult.Swap.Invoice,
            ExpectedAmount = amountSats,
            ContractScript = contractScript,
            Contract = arkWalletContract!,
            Status = ArkSwapStatus.Pending,
            Hash = new uint256(swapResult.Hash).ToString()
        };
        await dbContext.Swaps.AddAsync(reverseSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishUpdates(new ArkSwapUpdated { Swap = reverseSwap });
        return reverseSwap;
    }

    public async Task<ArkSwap> CreateSubmarineSwap(string walletId, BOLT11PaymentRequest paymentRequest, CancellationToken cancellationToken )
    {
        // Validate amount against Boltz limits
        var amountSats = (long)(paymentRequest.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
        var (isValid, errorMessage) = await ValidateAmountAsync(amountSats, isReverse: false, cancellationToken);
        if (!isValid)
        {
            throw new InvalidOperationException(errorMessage);
        }

        if (!await walletService.CanHandle(walletId, cancellationToken))
        {
            throw new InvalidOperationException("No signer found for wallet");
        }
        
        var signer = await walletService.CreateSigner(walletId, cancellationToken);

        await using var dbContext = dbContextFactory.CreateContext();
        
        var swap = await dbContext.Swaps.FirstOrDefaultAsync(s => s.Invoice == paymentRequest.ToString(), cancellationToken);
        if (swap != null)
        {
            return swap;
        }
        
        
        SubmarineSwapResult? swapResult = null;
        ArkWalletContract? arkWalletContract = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var sender = await signer.GetPublicKey(cancellationToken);

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateSubmarineSwap(
                paymentRequest,
                sender,
                cancellationToken: cancellationToken);
            
            try
            {
                var (isValid, errorMessage) = await ValidateFeesAsync(amountSats, swapResult.Swap.ExpectedAmount, isReverse: false, cancellationToken);
                if (!isValid)
                {
                    throw new InvalidOperationException(errorMessage);
                }
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                // Log but don't fail if we can't validate fees (e.g., network issues)
                logger.LogWarning(e, "Unable to validate submarine swap fees, proceeding anyway");
            }
            
            var contractScript = swapResult.Contract.GetArkAddress().ScriptPubKey.ToHex();
            arkWalletContract = new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.Contract.Type,
                Active = true,
                ContractData = swapResult.Contract.GetContractData()
            };

            return (arkWalletContract, swapResult.Contract);
        }, cancellationToken);

        if (swapResult is null || arkWalletContract is null || contract is not VHTLCContract htlcContract) 
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex(); 
        dbContext.ChangeTracker.TrackGraph(arkWalletContract, node => node.Entry.State = EntityState.Unchanged);
        var submarineSwap = new ArkSwap
        {
            SwapId = swapResult.Swap.Id,
            WalletId = walletId,
            SwapType =  ArkSwapType.Submarine,
            Invoice = paymentRequest.ToString(),
            ExpectedAmount = swapResult.Swap.ExpectedAmount,
            ContractScript = contractScript,
            Contract = arkWalletContract!,
            Status = ArkSwapStatus.Pending,
            Hash = paymentRequest.Hash.ToString()
        };
        await dbContext.Swaps.AddAsync(submarineSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishUpdates(new ArkSwapUpdated { Swap = submarineSwap });
        
        await arkadeSpender.Spend(walletId, [ new TxOut(Money.Satoshis(submarineSwap.ExpectedAmount), htlcContract.GetArkAddress())], cancellationToken);
        
        return submarineSwap;
    }
    
    /// <summary>
    /// Gets cached Boltz limits, fetching from API if cache is expired or empty
    /// </summary>
    public async Task<BoltzLimitsCache> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        await _limitsCacheLock.WaitAsync(cancellationToken);
        try
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            // Return cached limits if still valid
            if (_limitsCache != null && DateTimeOffset.UtcNow < _limitsCache.ExpiresAt)
            {
                return _limitsCache;
            }
            _limitsCache = null;
            // Fetch fresh limits from Boltz API
            try
            {
                var submarinePairsTask = boltzClient.GetSubmarinePairsAsync(cts.Token);
                var reversePairsTask = boltzClient.GetReversePairsAsync(cts.Token);

                await Task.WhenAll(submarinePairsTask, reversePairsTask);
                
                var submarinePairs = await submarinePairsTask;
                var reversePairs = await reversePairsTask;
                
                if (submarinePairs?.ARK?.BTC != null && reversePairs?.BTC?.ARK != null)
                {
                    _limitsCache = new BoltzLimitsCache
                    {
                        // Submarine: Ark → Lightning (sending)
                        SubmarineMinAmount = submarinePairs.ARK.BTC.Limits?.Minimal ?? 0,
                        SubmarineMaxAmount = submarinePairs.ARK.BTC.Limits?.Maximal ?? long.MaxValue,
                        // Boltz API returns percentage as 0.01 for 0.01%, so divide by 100 to get decimal multiplier
                        SubmarineFeePercentage = (submarinePairs.ARK.BTC.Fees?.Percentage ?? 0) / 100m,
                        SubmarineMinerFee = submarinePairs.ARK.BTC.Fees?.MinerFeesValue ?? 0,
                        
                        // Reverse: Lightning → Ark (receiving)
                        ReverseMinAmount = reversePairs.BTC.ARK.Limits?.Minimal ?? 0,
                        ReverseMaxAmount = reversePairs.BTC.ARK.Limits?.Maximal ?? long.MaxValue,
                        // Boltz API returns percentage as 0.01 for 0.01%, so divide by 100 to get decimal multiplier
                        ReverseFeePercentage = (reversePairs.BTC.ARK.Fees?.Percentage ?? 0) / 100m,
                        ReverseMinerFee = reversePairs.BTC.ARK.Fees?.MinerFees?.Claim ?? 0,
                        
                        FetchedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow.Add(LimitsCacheExpiry)
                    };

                    logger.LogInformation("Fetched Boltz limits - Submarine: {SubMin}-{SubMax} sats, Reverse: {RevMin}-{RevMax} sats",
                        _limitsCache.SubmarineMinAmount, _limitsCache.SubmarineMaxAmount,
                        _limitsCache.ReverseMinAmount, _limitsCache.ReverseMaxAmount);
                    
                    return _limitsCache;
                }
                else
                {
                    throw new InvalidOperationException("Boltz instance does not support Ark");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch Boltz limits");
                throw new InvalidOperationException("Failed to fetch Boltz limits");
            }

        }
        finally
        {
            _limitsCacheLock.Release();
        }
    }

    /// <summary>
    /// Validates if an amount is within Boltz limits for the specified swap type
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateAmountAsync(long amountSats, bool isReverse, CancellationToken cancellationToken = default)
    {
        var limits = await GetLimitsAsync(cancellationToken);
        if (limits == null)
        {
            return (false, "Unable to fetch Boltz limits");
        }

        var (minAmount, maxAmount, swapType) = isReverse 
            ? (limits.ReverseMinAmount, limits.ReverseMaxAmount, "receiving")
            : (limits.SubmarineMinAmount, limits.SubmarineMaxAmount, "sending");

        if (amountSats < minAmount)
        {
            return (false, $"Amount {amountSats} sats is below minimum {minAmount} sats for {swapType} Lightning");
        }

        if (amountSats > maxAmount)
        {
            return (false, $"Amount {amountSats} sats exceeds maximum {maxAmount} sats for {swapType} Lightning");
        }

        return (true, null);
    }
    
    /// <summary>
    /// Validates if the actual swap fee is within acceptable range compared to expected fee
    /// </summary>
    /// <param name="amountSats">The invoice/payment amount in satoshis</param>
    /// <param name="actualSwapAmount">The actual onchain/expected amount from Boltz</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple indicating if fees are valid and optional error message</returns>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateFeesAsync(long amountSats, long actualSwapAmount, bool isReverse, CancellationToken cancellationToken = default)
    {
        var limits = await GetLimitsAsync(cancellationToken);
        if (limits == null)
        {
            return (false, "Unable to fetch Boltz limits");
        }

        // Calculate actual fee based on swap type
        // Reverse: user receives actualSwapAmount onchain, pays amountSats via Lightning
        // Submarine: user pays actualSwapAmount onchain, receives amountSats via Lightning
        var actualFee = isReverse 
            ? amountSats - actualSwapAmount  // Reverse: Lightning amount - onchain amount
            : actualSwapAmount - amountSats; // Submarine: onchain amount - Lightning amount
        
        var (feePercentage, minerFee, swapType) = isReverse
            ? (limits.ReverseFeePercentage, limits.ReverseMinerFee, "Reverse")
            : (limits.SubmarineFeePercentage, limits.SubmarineMinerFee, "Submarine");
        
        // Calculate expected fee: (amount × percentage) + miner fee
        var expectedFee = (long)(amountSats * feePercentage) + minerFee;
        var feeToleranceSats = 100; // Allow 100 sat tolerance for rounding
        
        // Only fail if actual fee is HIGHER than expected (allow lower fees)
        if (actualFee > expectedFee + feeToleranceSats)
        {
            logger.LogWarning("{SwapType} swap fee too high: expected ~{ExpectedFee} sats ({FeePercentage}% + {MinerFee} sats miner fee), got {ActualFee} sats", 
                swapType, expectedFee, feePercentage * 100, minerFee, actualFee);
            return (false, 
                $"Boltz fee verification failed. Expected ~{expectedFee} sats ({feePercentage * 100:F2}% + {minerFee} sats miner fee), but swap would charge {actualFee} sats");
        }
        
        if (actualFee < expectedFee - feeToleranceSats)
        {
            logger.LogInformation("{SwapType} swap fee lower than expected: {ActualFee} sats vs expected {ExpectedFee} sats - accepting", 
                swapType, actualFee, expectedFee);
        }
        
        logger.LogInformation("{SwapType} swap fee verified: {ActualFee} sats ({FeePercentage}% + {MinerFee} sats miner fee)", 
            swapType, actualFee, feePercentage * 100, minerFee);

        return (true, null);
    }
}

public class BoltzLimitsCache
{
    // Submarine swap limits (Ark → Lightning, sending)
    public long SubmarineMinAmount { get; set; }
    public long SubmarineMaxAmount { get; set; }
    public decimal SubmarineFeePercentage { get; set; }
    public long SubmarineMinerFee { get; set; }
    
    // Reverse swap limits (Lightning → Ark, receiving)
    public long ReverseMinAmount { get; set; }
    public long ReverseMaxAmount { get; set; }
    public decimal ReverseFeePercentage { get; set; }
    public long ReverseMinerFee { get; set; }
    
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}