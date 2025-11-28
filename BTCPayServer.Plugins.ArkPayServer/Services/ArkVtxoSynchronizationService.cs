using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NArk;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkVtxoSynchronizationService(
    ILogger<ArkVtxoSynchronizationService> logger,
    AsyncKeyedLocker asyncKeyedLocker,
    EventAggregator eventAggregator,
    TrackedContractsCache contractsCache,
    ArkPluginDbContextFactory arkPluginDbContextFactory,
    IndexerService.IndexerServiceClient indexerClient) : EventHostedServiceBase(eventAggregator, logger)
{
    private Task? _lastListeningLoop = null;
    private string? _subscriptionId = null;
    private readonly TaskCompletionSource _startedTcs = new();
    private CancellationTokenSource? _cts = null;
    private HashSet<string> _lastSubscribedScripts = new();
    private Task? _watchdogTask = null;
    
    public Task Started => _startedTcs.Task;
    public bool IsActive => _lastListeningLoop is { IsCompleted: false };
    protected override void SubscribeToEvents()
    {
        Subscribe<ArkCacheUpdated>();
    }
    
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await base.StartAsync(cancellationToken);
        
        // Immediately establish subscription with current contracts
        // This ensures we don't miss any events while waiting for cache updates
        PushEvent(new ArkCacheUpdated(nameof(TrackedContractsCache)));
        
        // Start watchdog to ensure connection stays alive when there are scripts to track
        _watchdogTask = WatchdogLoop(_cts.Token);
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts?.Dispose();
        }
        
        if (_watchdogTask != null)
        {
            try { await _watchdogTask; } catch { /* expected on cancellation */ }
        }
     
        await base.StopAsync(cancellationToken);
    }
    
    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is ArkCacheUpdated cacheUpdated)
        {
            await HandleCacheUpdate(cacheUpdated, cancellationToken);
        }
    }
    
    private async Task HandleCacheUpdate(ArkCacheUpdated waitForCacheUpdate, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received cache update event: CacheName={CacheName}", 
            waitForCacheUpdate.CacheName);
            
        if (waitForCacheUpdate.CacheName is not nameof(TrackedContractsCache))
        {
            logger.LogInformation("Ignoring cache update for {CacheName}", waitForCacheUpdate.CacheName);
            return;
        }
            
        var contracts = contractsCache.Contracts;
        var payouts = contractsCache.Payouts;
        
        var subscribedContractScripts = contracts.Select(c => c.Script).ToHashSet();
        var subscribedPayoutScripts = payouts.Select(GetPayoutScript).ToHashSet();
        
        // Also subscribe to non-spent VTXO scripts to detect when they are spent or swept
        await using var dbContext = arkPluginDbContextFactory.CreateContext();
        var nonSpentVtxoScripts = await dbContext.Vtxos
            .Where(v => v.SpentByTransactionId == null)
            .Select(v => v.Script)
            .Distinct()
            .ToListAsync(cancellationToken);

        var subscribedScripts = subscribedContractScripts
            .Concat(subscribedPayoutScripts)
            .Concat(nonSpentVtxoScripts)
            .ToHashSet();
        
        logger.LogInformation(
            "Updating subscription with {ActiveContractsCount} active contracts ({ContractScripts}), {PendingPayoutsCount} pending payouts, and {NonSpentVtxosCount} non-spent VTXOs.",
            subscribedContractScripts.Count,
            string.Join(", ", subscribedContractScripts.Take(5)),
            subscribedPayoutScripts.Count,
            nonSpentVtxoScripts.Count
        );

        // Skip if no scripts to track
        if (subscribedScripts.Count == 0)
        {
            logger.LogInformation("No scripts to track. Skipping subscription.");
            _lastSubscribedScripts.Clear();
            _startedTcs.TrySetResult();
            return;
        }
        
        // Skip if scripts haven't changed
        if (subscribedScripts.SetEquals(_lastSubscribedScripts))
        {
            logger.LogDebug("Scripts unchanged. Ensuring listener is active.");
            // Still ensure listener is running
            if (_subscriptionId != null && _lastListeningLoop is null or { IsCompleted: true })
            {
                StartListening(_subscriptionId, _cts!.Token);
            }
            _startedTcs.TrySetResult();
            return;
        }

        var req = new SubscribeForScriptsRequest
        {
            SubscriptionId = _subscriptionId ?? string.Empty
        };

        req.Scripts.AddRange(subscribedScripts);

        try
        {
            var subscribeRes = await indexerClient.SubscribeForScriptsAsync(req, cancellationToken: cancellationToken);
            var isNewSubscription = _subscriptionId != subscribeRes.SubscriptionId;
            _subscriptionId = subscribeRes.SubscriptionId;
            _lastSubscribedScripts = subscribedScripts;
            logger.LogInformation("Successfully subscribed with ID: {SubscriptionId} (New: {IsNew})", _subscriptionId, isNewSubscription);
            
            // Always ensure listener is running, especially for new subscriptions
            if (isNewSubscription || _lastListeningLoop is null or { IsCompleted: true })
            {
                StartListening(subscribeRes.SubscriptionId, _cts!.Token);
            }
            
            _startedTcs.TrySetResult();
            
            // Re-read cache after subscribing to catch any contracts added during subscription
            var currentContracts = contractsCache.Contracts;
            var currentPayouts = contractsCache.Payouts;
            var currentContractScripts = currentContracts.Select(c => c.Script).ToHashSet();
            var currentPayoutScripts = currentPayouts.Select(GetPayoutScript).ToHashSet();
            
            // Re-read non-spent VTXOs
            var currentNonSpentVtxoScripts = await dbContext.Vtxos
                .Where(v => v.SpentByTransactionId == null)
                .Select(v => v.Script)
                .Distinct()
                .ToListAsync(cancellationToken);
            
            var currentScripts = currentContractScripts
                .Concat(currentPayoutScripts)
                .Concat(currentNonSpentVtxoScripts)
                .ToHashSet();
            
            // If cache changed during subscription, trigger another update
            if (!currentScripts.SetEquals(subscribedScripts))
            {
                logger.LogWarning("Cache changed during subscription. Triggering immediate re-subscription. " +
                                 "Old: {OldCount}, New: {NewCount}", 
                                 subscribedScripts.Count, currentScripts.Count);
                PushEvent(new ArkCacheUpdated(nameof(TrackedContractsCache)));
            }
            
            // Protected polling - don't let exceptions abort reconnection logic
            try
            {
                await PollScriptsForVtxos(currentScripts, _cts!.Token);
            }
            catch (Exception pollEx)
            {
                logger.LogWarning(pollEx, "Failed to poll scripts after subscription. Will retry on next event.");
            }
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "Failed to subscribe to scripts. Will retry after delay.");
            _subscriptionId = null;
            _lastSubscribedScripts.Clear();
            
            // Protected polling - even if this fails, we still want to retry subscribe
            try
            {
                await PollScriptsForVtxos(subscribedScripts, _cts!.Token);
            }
            catch (Exception pollEx)
            {
                logger.LogWarning(pollEx, "Failed to poll scripts after subscribe failure.");
            }
            
            // Backoff before retry to avoid hot loop
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Retry delay cancelled during shutdown.");
                return;
            }
            
            PushEvent(new ArkCacheUpdated(nameof(TrackedContractsCache)));
        }
    }
    
    private string GetPayoutScript(PayoutData payout)
    {
        return ArkAddress.Parse(payout.DedupId!).ScriptPubKey.ToHex();
    }

    private void StartListening(string subscriptionId, CancellationToken stoppingToken)
    {
        // Check if listener is still alive - only use IsCompleted for reliability
        if (_lastListeningLoop is { IsCompleted: false })
        {
            logger.LogDebug("Listener already active for subscription ID: {SubscriptionId}", subscriptionId);
            return;
        }
        
        if (_lastListeningLoop?.IsCompleted == true)
        {
            logger.LogWarning("Previous listener completed. Restarting with subscription ID: {SubscriptionId}", subscriptionId);
        }

        _lastListeningLoop = ListenToStream(subscriptionId, stoppingToken);
        logger.LogInformation("Stream listener started with subscription ID: {SubscriptionId}", subscriptionId);
    }

    private async Task ListenToStream(string subscriptionId, CancellationToken token)
    {
        var streamCompletedNormally = false;
        try
        {
            logger.LogInformation("Connecting to stream with subscription ID: {SubscriptionId}", subscriptionId);
            var stream = indexerClient.GetSubscription(new GetSubscriptionRequest { SubscriptionId = subscriptionId }, cancellationToken: token);

            await foreach (var response in stream.ResponseStream.ReadAllAsync(token))
            {
                if (response == null) continue;
                switch (response.DataCase)
                {
                    case GetSubscriptionResponse.DataOneofCase.None:
                        break;
                    case GetSubscriptionResponse.DataOneofCase.Heartbeat:
                        logger.LogTrace("Received heartbeat for subscription {SubscriptionId}", subscriptionId);
                        break;
                    case GetSubscriptionResponse.DataOneofCase.Event when response.Event is not null :
                        await PollScriptsForVtxos( response.Event.Scripts.ToHashSet(), token);
                        logger.LogDebug("Received update for {Count} scripts.", response.Event.Scripts.Count);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            
            // If we reach here, the stream completed normally (disconnect)
            streamCompletedNormally = true;
            logger.LogWarning("Stream completed unexpectedly for subscription {SubscriptionId}. Triggering reconnection.", subscriptionId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger.LogInformation("Stream was cancelled for subscription {SubscriptionId}.", subscriptionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stream listener failed for subscription {SubscriptionId}. Triggering immediate reconnection.", subscriptionId);
            // Clear subscription ID to force re-subscription (might be invalid/stale)
            _subscriptionId = null;
            _lastSubscribedScripts.Clear();
        }
        finally
        {
            logger.LogInformation("ListenToStream finished for subscription {SubscriptionId}.", subscriptionId);
            
            // Trigger reconnection if stream completed (disconnect) or failed, but not if cancelled
            if (streamCompletedNormally || (!token.IsCancellationRequested && _lastListeningLoop?.IsCompleted == true))
            {
                // Clear subscription ID on unexpected completion too (might be server-side issue)
                if (streamCompletedNormally)
                {
                    logger.LogWarning("Clearing subscription ID due to unexpected stream completion.");
                    _subscriptionId = null;
                    _lastSubscribedScripts.Clear();
                }
                logger.LogInformation("Triggering reconnection for subscription {SubscriptionId}", subscriptionId);
                PushEvent(new ArkCacheUpdated(nameof(TrackedContractsCache)));
            }
        }
    }
    
    public async Task PollScriptsForVtxos(IReadOnlySet<string> allScripts, CancellationToken cancellationToken)
    {
        if (allScripts.Count == 0)
            return;

        using var l = await asyncKeyedLocker.LockAsync($"script-sync-lock", cancellationToken);

        // Query scripts in 1000 script chunks
        foreach (var scripts in allScripts.Chunk(1000))
        {
            var request = new GetVtxosRequest()
            {
                Scripts = { scripts },
                RecoverableOnly = false,
                SpendableOnly = false,
                SpentOnly = false,
                Page = new IndexerPageRequest()
                {
                    Index = 0,
                    Size = 1000
                }
            };

            await using var dbContext = arkPluginDbContextFactory.CreateContext();

            var existingVtxos =
                await dbContext
                    .Vtxos
                    .Where(x => scripts.Contains(x.Script))
                    .ToListAsync(cancellationToken: cancellationToken);

            var vtxosUpdated = new List<VTXO>();

            GetVtxosResponse? response = null;

            while (response is null || response.Page.Next != response.Page.Total)
            {
                response = await indexerClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

                var vtxosToProcess = new Queue<IndexerVtxo>(response.Vtxos);

                while (vtxosToProcess.TryDequeue(out var vtxoToProccess))
                {
                    if (existingVtxos.Find(v =>
                            v.TransactionId == vtxoToProccess.Outpoint.Txid &&
                            v.TransactionOutputIndex == (int)vtxoToProccess.Outpoint.Vout) is { } existing)
                    {
                        // Compute hash before and after to detect actual changes
                        var hashBefore = existing.GetHashCode();
                        Map(vtxoToProccess, existing);
                        var hashAfter = existing.GetHashCode();
                        
                        // Only publish if the VTXO actually changed
                        if (hashBefore != hashAfter)
                        {
                            vtxosUpdated.Add(existing);
                        }
                    }
                    else
                    {
                        var newVtxo = Map(vtxoToProccess);
                        await dbContext.Vtxos.AddAsync(newVtxo, cancellationToken);
                        vtxosUpdated.Add(newVtxo);
                    }
                }

                request.Page.Index = response.Page.Next;
            }


            await dbContext.SaveChangesAsync(cancellationToken);
            if (vtxosUpdated.Count != 0)
            {
                eventAggregator.Publish(new VTXOsUpdated([.. vtxosUpdated]));
            }
        }
    }

    public static VTXO Map(IndexerVtxo vtxo, VTXO? existing = null)
    {
        var isNew = existing == null;
        existing ??= new VTXO();

        // Only set properties if they differ to avoid triggering EF change tracking unnecessarily
        if (isNew || existing.TransactionId != vtxo.Outpoint.Txid)
            existing.TransactionId = vtxo.Outpoint.Txid;
        
        if (isNew || existing.TransactionOutputIndex != (int)vtxo.Outpoint.Vout)
            existing.TransactionOutputIndex = (int)vtxo.Outpoint.Vout;
        
        if (isNew || existing.Amount != (long)vtxo.Amount)
            existing.Amount = (long)vtxo.Amount;
        
        var recoverable = vtxo.IsSwept || DateTimeOffset.FromUnixTimeSeconds(vtxo.ExpiresAt) < DateTimeOffset.UtcNow;
        if (isNew || existing.Recoverable != recoverable)
            existing.Recoverable = recoverable;
        
        var seenAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt);
        if (isNew || existing.SeenAt != seenAt)
            existing.SeenAt = seenAt;
        
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.ExpiresAt);
        if (isNew || existing.ExpiresAt != expiresAt)
            existing.ExpiresAt = expiresAt;
        
        var spentBy = string.IsNullOrEmpty(vtxo.SpentBy) ? null : vtxo.SpentBy;
        if (isNew || existing.SpentByTransactionId != spentBy)
            existing.SpentByTransactionId = spentBy;
        
        var settledBy = string.IsNullOrEmpty(vtxo.SettledBy) ? null : vtxo.SettledBy;
        if (isNew || existing.SettledByTransactionId != settledBy)
            existing.SettledByTransactionId = settledBy;
        
        if (isNew || existing.Script != vtxo.Script)
            existing.Script = vtxo.Script;

        return existing;
    }
    
    private async Task WatchdogLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                
                var contracts = contractsCache.Contracts;
                var payouts = contractsCache.Payouts;
                
                // Check if there are non-spent VTXOs to track
                await using var dbContext = arkPluginDbContextFactory.CreateContext();
                var hasNonSpentVtxos = await dbContext.Vtxos
                    .AnyAsync(v => v.SpentByTransactionId == null, cancellationToken);
                
                var hasScripts = contracts.Count > 0 || payouts.Count > 0 || hasNonSpentVtxos;
                
                if (hasScripts && _lastListeningLoop is null or { IsCompleted: true })
                {
                    logger.LogWarning("Watchdog detected inactive listener with scripts to track. Triggering reconnection.");
                    PushEvent(new ArkCacheUpdated(nameof(TrackedContractsCache)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Watchdog stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Watchdog encountered unexpected error.");
        }
    }


}