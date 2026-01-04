using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Abstractions;
using NArk.Extensions;
using NArk.Helpers;
using NArk.Models;
using NArk.Services;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Service for managing Ark intents with automatic submission, event monitoring, and batch participation
/// </summary>
public class ArkIntentService(
    EventAggregator eventAggregator,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkServiceClient,
    ArkadeWalletSignerProvider signerProvider,
    ArkWalletService arkWalletService,
    CachedServerInfoService serverInfoService,
    IClientTransport clientTransport,
    TransactionHelpers.ArkTransactionBuilder arkTransactionBuilder,
    ArkadeSpender arkadeSpender,
    ILogger<ArkIntentService> logger)
    : IHostedService, IDisposable
{
    // Polling intervals
    private static readonly TimeSpan SubmissionPollingInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EventStreamRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultIntentExpiry = TimeSpan.FromMinutes(5);
    
    private readonly ConcurrentDictionary<string, ArkIntent> _activeIntents = new();
    private readonly ConcurrentDictionary<string, BatchSession> _activeBatchSessions = new();
    private CancellationTokenSource? _serviceCts;
    private CancellationTokenSource? _eventStreamCts;
    private Task? _submissionTask;
    private Task? _eventStreamTask;

    private Timer? _submissionTriggerTimer;
    private bool _disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting ArkIntentService");
        
        _serviceCts = new CancellationTokenSource();
        
        
        _submissionTask = AutoSubmitIntentsAsync(_serviceCts.Token);
        // Start automatic submission task
        _submissionTriggerTimer = new Timer(_ => TriggerSubmissionCheck(), null, TimeSpan.FromSeconds(1),
            SubmissionPollingInterval);
        
        // Load existing WaitingForBatch intents and start shared event stream
        await LoadActiveIntentsAsync(cancellationToken);
        _ = RunSharedEventStreamController(_serviceCts.Token);
        
        logger.LogInformation("ArkIntentService started");
    }

    private async Task RunSharedEventStreamController(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_eventStreamCts is not null)
                await _eventStreamCts.CancelAsync();
            if (_eventStreamTask?.IsCompleted is null or true)
                _eventStreamTask = RunSharedEventStreamAsync(cancellationToken);
            await eventAggregator.WaitNext<IntentsUpdated>(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping ArkIntentService");
        if(_serviceCts != null)
            await _serviceCts!.CancelAsync();
        
        // Stop event stream
        if (_eventStreamCts != null)
            await _eventStreamCts.CancelAsync();
        
        // Wait for tasks to complete
        if (_submissionTask != null)
            await _submissionTask;
        if (_eventStreamTask != null)
            await _eventStreamTask;
        
        _activeIntents.Clear();
        _activeBatchSessions.Clear();
        logger.LogInformation("ArkIntentService stopped");
    }

    /// <summary>
    /// Create a new intent by generating BIP322 signatures
    /// </summary>
    public async Task<string> CreateIntentAsync(
        string walletId,
        ArkPsbtSigner[] coins,
        ArkTxOut[]? outputs,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        CancellationToken cancellationToken = default)
    {
        outputs ??= await GetDefaultOutputs(coins.Sum(c => c.Coin.Amount), walletId, cancellationToken);        
        
        await using var dbContext = dbContextFactory.CreateContext();

        // Check if any VTXOs are already locked
        var coinTxIds = coins.Select(c => c.Coin.Outpoint.Hash.ToString()).ToHashSet();

        var potentiallyLockedVtxos = await dbContext.IntentVtxos
            .Include(iv => iv.Intent)
            .Include(iv => iv.Vtxo)
            .Where(iv => (iv.Intent.State == ArkIntentState.WaitingToSubmit || iv.Intent.State == ArkIntentState.WaitingForBatch) &&
                         coinTxIds.Contains(iv.VtxoTransactionId))
            .Select(iv => iv.Vtxo)
            .ToListAsync(cancellationToken);

        // Filter in memory to check exact outpoint matches
        var coinOutpointSet = coins.Select(c => $"{c.Coin.Outpoint.Hash}:{c.Coin.Outpoint.N}").ToHashSet();
        var lockedVtxos = potentiallyLockedVtxos.Where(v => coinOutpointSet.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}")).ToList();

        if (lockedVtxos.Any())
        {
            throw new InvalidOperationException(
                $"One or more VTXOs are already locked by another intent: {string.Join(", ", lockedVtxos.Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}"))}");
        }

        // Get signer
        var signer = await signerProvider.GetSigner(walletId, cancellationToken);
        if (signer == null)
        {
            throw new InvalidOperationException($"Signer not available for wallet {walletId}");
        }
        var vtxoScripts = coins.Select(c => c.Coin.Contract.GetArkAddress().ScriptPubKey.ToHex()).ToList();
        // ensure the wallet has the contract of the vtxos in question
       
        var contracts =
            await dbContext.WalletContracts
                .Where(wc => wc.WalletId == walletId && vtxoScripts.Contains(wc.Script))
                .ToDictionaryAsync(contract => contract.Script, cancellationToken);
       
        if (contracts.Count != vtxoScripts.Count)
       
        {
            throw new InvalidOperationException($"One or more VTXOs are not owned by wallet {walletId}");
        } 
       

        // Generate intent transactions with BIP322 signatures
        var effectiveValidFrom = validFrom ?? DateTimeOffset.UtcNow;
        var effectiveValidUntil = validUntil ?? DateTimeOffset.UtcNow.Add(DefaultIntentExpiry);

        var cosigners = new[] { await signer.GetPublicKey(cancellationToken) };
        var serverInfo = await serverInfoService.GetServerInfoAsync(cancellationToken);
        var (registerTx, deleteTx, registerMsg, deleteMsg) = await CreateIntentsAsync(
            serverInfo.Network,
            cosigners,
            effectiveValidFrom,
            effectiveValidUntil,
            coins,
            outputs,
            cancellationToken);

        // Convert coins to VTXO entities for database storage
        var vtxoEntities = coins.Select(coin => new VTXO
        {
            TransactionId = coin.Coin.Outpoint.Hash.ToString(),
            TransactionOutputIndex = (int)coin.Coin.Outpoint.N,
            Amount = coin.Coin.TxOut.Value.Satoshi,
            Script = coin.Coin.TxOut.ScriptPubKey.ToHex(),
            SeenAt = DateTimeOffset.UtcNow,

            Recoverable = coin.Coin.Recoverable
        }).ToList();
        
        foreach (var entityEntry in vtxoEntities.Select(dbContext.Entry))
        {
            entityEntry.State = EntityState.Modified;
        }

        // Create intent entity (IntentId will be set by server on submission)
        var intent = new ArkIntent
        {
            WalletId = walletId,
            State = ArkIntentState.WaitingToSubmit,
            ValidFrom = effectiveValidFrom,
            ValidUntil = effectiveValidUntil,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            IntentVtxos = vtxoEntities.Select(v => new ArkIntentVtxo
            {
                VtxoTransactionId = v.TransactionId,
                VtxoTransactionOutputIndex = v.TransactionOutputIndex,
                Vtxo = v,
                LinkedAt = DateTimeOffset.UtcNow
            }).ToList(),
            RegisterProof = registerTx.ToBase64(),
            RegisterProofMessage = registerMsg,
            DeleteProof = deleteTx.ToBase64(),
            DeleteProofMessage = deleteMsg
        };

        await dbContext.Intents.AddAsync(intent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        
        logger.LogInformation("Created intent {InternalId} for wallet {WalletId}", intent.InternalId, walletId);
        
        // Trigger immediate submission check if intent is already valid
        if (effectiveValidFrom <= DateTimeOffset.UtcNow)
        {
            TriggerSubmissionCheck();
        }
        
        return intent.InternalId.ToString();
    }

    private async Task<ArkTxOut[]> GetDefaultOutputs(Money totalAmount, string destinationWalletId, CancellationToken cancellationToken)
    {
        var wallet = await arkWalletService.GetWallet(destinationWalletId, cancellationToken);

        if (wallet is null) throw new Exception("Destination wallet did not exist");

        // Default: send all funds back to wallet (refreshes VTXOs, moves from recoverable state, etc.)
        var destination = await arkadeSpender.GetDestination(wallet, await serverInfoService.GetServerInfoAsync(cancellationToken));

        return [
            new ArkTxOut(ArkTxOutType.Vtxo, totalAmount, destination.ScriptPubKey.GetDestination()!)
        ];
    }

    private void TriggerSubmissionCheck()
    {
        eventAggregator.Publish(new IntentSubmissionRequired());
    }
    
    private void TriggerStreamUpdate()
    {
        eventAggregator.Publish(new IntentsUpdated());
    }

    /// <summary>
    /// Cancel an intent by submitting the delete proof
    /// </summary>
    public async Task CancelIntentAsync(string internalId, string reason, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var intent = await dbContext.Intents
            .Include(i => i.IntentVtxos)
                .ThenInclude(iv => iv.Vtxo)
            .FirstOrDefaultAsync(i => i.InternalId == int.Parse(internalId), cancellationToken);
        
        if (intent == null)
        {
            logger.LogWarning("Intent {IntentId} not found for cancellation", internalId);
            return;
        }

        switch (intent.State)
        {
            case ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled:
                logger.LogWarning("Intent {IntentId} is already in final state {State}", internalId, intent.State);
                return;
            case ArkIntentState.BatchInProgress:
                logger.LogWarning("Intent {IntentId} cannot be cancelled - batch is in progress", internalId);
                throw new InvalidOperationException("Cannot cancel intent while batch is in progress");
            // Submit delete proof if intent was submitted
            case ArkIntentState.WaitingForBatch:
                try
                {
                    var deleteRequest = new DeleteIntentRequest
                    {
                        Intent = new Intent()
                        {
                            Message = intent.DeleteProofMessage,
                            Proof = intent.DeleteProof
                        }
                    };
                
                    await arkServiceClient.DeleteIntentAsync(deleteRequest, cancellationToken: cancellationToken);
                    logger.LogInformation("Submitted delete proof for intent {IntentId}", internalId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to submit delete proof for intent {IntentId}", internalId);
                }

                break;
        }

        intent.State = ArkIntentState.Cancelled;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        // Remove from active intents and trigger stream restart
        _ = intent.IntentId is not null && _activeIntents.TryRemove(intent.IntentId, out _);
        _ = intent.IntentId is not null && _activeBatchSessions.TryRemove(intent.IntentId!, out _);
        TriggerStreamUpdate();
        logger.LogInformation("Intent {IntentId} cancelled: {Reason}", internalId, reason);
    }

    // /// <summary>
    // /// Check if a wallet has any pending intents
    // /// </summary>
    // public async Task<bool> HasPendingIntentAsync(string walletId, CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
    //     return await dbContext.Intents
    //         .AnyAsync(i => i.WalletId == walletId && 
    //                       (i.State == ArkIntentState.WaitingToSubmit || i.State == ArkIntentState.WaitingForBatch),
    //                  cancellationToken);
    // }

    #region Private Methods

    private async Task AutoSubmitIntentsAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting automatic intent submission loop");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await eventAggregator.WaitNext<IntentSubmissionRequired>(cancellationToken);
                
                await using var dbContext = dbContextFactory.CreateContext();
                
                var now = DateTimeOffset.UtcNow;
                var intentsToSubmit = await dbContext.Intents
                    .Include(i => i.IntentVtxos)
                        .ThenInclude(iv => iv.Vtxo)
                    .Where(i => i.State == ArkIntentState.WaitingToSubmit && 
                                i.ValidFrom <= now && 
                                i.ValidUntil > now)
                    .ToListAsync(cancellationToken);
                
                foreach (var intent in intentsToSubmit)
                {
                    try
                    {
                        await SubmitIntentAsync(intent, dbContext, cancellationToken);
                    }
                    catch (Exception ex) when (ex.Message.Contains("duplicated input"))
                    {
                        var vtxoTransactionIds = intent.IntentVtxos.Select(i => i.Vtxo.TransactionId).ToList();
                        var intentsToReview = await dbContext.Vtxos
                            .Where(v => vtxoTransactionIds.Contains(v.TransactionId))
                            .Include(v => v.IntentVtxos)
                            .ThenInclude(i => i.Intent)
                            .SelectMany(i => i.IntentVtxos)
                            .Select(iv => iv.Intent)
                            .ToListAsync(cancellationToken);

                        var intentsToCancel = intentsToReview
                            .Where(i => i.State == ArkIntentState.Cancelled || i.ValidUntil <= now)
                            .ToList();

                        try
                        {
                            foreach (var intentToCancel in intentsToCancel)
                            {
                                var deleteRequest = new DeleteIntentRequest
                                {
                                    Intent = new Intent()
                                    {
                                        Message = intent.DeleteProofMessage,
                                        Proof = intent.DeleteProof
                                    }
                                };
                
                                await arkServiceClient.DeleteIntentAsync(deleteRequest, cancellationToken: cancellationToken);
                                logger.LogInformation("Submitted delete proof for intent {IntentId}", intentToCancel.InternalId);
                            }
                        }
                        catch (Exception e)
                        { 
                            // ignored
                        }

                        try
                        {
                            await SubmitIntentAsync(intent, dbContext, cancellationToken);
                        }
                        catch (Exception resubmitException)
                        {
                            logger.LogError(resubmitException, "Failed to resubmit intent {InternalId}", intent.InternalId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to submit intent {InternalId}", intent.InternalId);
                    }
                }
                
                // Cancel expired intents
                var expiredIntents = await dbContext.Intents
                    .Where(i => (i.State == ArkIntentState.WaitingToSubmit || i.State == ArkIntentState.WaitingForBatch) && 
                                i.ValidUntil <= now)
                    .ToListAsync(cancellationToken);
                
                foreach (var intent in expiredIntents)
                {
                    intent.State = ArkIntentState.Cancelled;
                    intent.CancellationReason = "Expired";
                    intent.UpdatedAt = DateTimeOffset.UtcNow;
                }
                
                if (expiredIntents.Any())
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogInformation("Cancelled {Count} expired intents", expiredIntents.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in automatic intent submission loop");
            }
        }
        
        logger.LogInformation("Automatic intent submission loop stopped");
    }

    private async Task SubmitIntentAsync(ArkIntent intent, ArkPluginDbContext dbContext, CancellationToken cancellationToken)
    {
        logger.LogInformation("Submitting intent {InternalId}", intent.InternalId);
        
        // Check if any VTXOs have been spent before submission
        var spentVtxos = intent.IntentVtxos
            .Select(iv => iv.Vtxo)
            .Where(v => v.SpentByTransactionId != null)
            .ToList();
        
        if (spentVtxos.Any())
        {
            var spentOutpoints = string.Join(", ", spentVtxos.Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}"));
            logger.LogWarning("Intent {InternalId} has spent VTXOs: {SpentVtxos}", intent.InternalId, spentOutpoints);
            
            intent.State = ArkIntentState.Cancelled;
            intent.CancellationReason = $"VTXOs spent before submission: {spentOutpoints}";
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        
        var registerRequest = new RegisterIntentRequest
        {
            Intent = new Intent()
            {
                Message = intent.RegisterProofMessage,
                Proof = intent.RegisterProof
            }
        };

        var response = await arkServiceClient.RegisterIntentAsync(registerRequest, cancellationToken: cancellationToken);
        
        // Update with server-assigned ID
        intent.IntentId = response.IntentId;
        intent.State = ArkIntentState.WaitingForBatch;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        logger.LogInformation("Intent submitted successfully with ID {IntentId}", intent.IntentId);
        
        // Add to active intents and trigger stream restart
        _activeIntents[intent.IntentId] = intent;
        TriggerStreamUpdate();
    }

    private async Task LoadActiveIntentsAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading active intents");
        
        await using var dbContext = dbContextFactory.CreateContext();
        
        var waitingIntents = await dbContext.Intents
            .Include(i => i.IntentVtxos)
                .ThenInclude(iv => iv.Vtxo)
            .Where(i => i.State == ArkIntentState.WaitingForBatch)
            .ToListAsync(cancellationToken);
        
        foreach (var intent in waitingIntents)
        {
            _activeIntents[intent.IntentId!] = intent;
        }
        
        logger.LogInformation("Loaded {Count} active intents", waitingIntents.Count);
    }

    private async Task RunSharedEventStreamAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Starting shared event stream");

            try
            {
                // Build topics from all active intents (VTXOs + cosigner public keys)
                var vtxoTopics = _activeIntents.Values
                    .SelectMany(intent => intent.IntentVtxos
                        .Select(iv => $"{iv.VtxoTransactionId}:{iv.VtxoTransactionOutputIndex}"));

                var cosignerTopics = _activeIntents.Values
                    .SelectMany(intent => ExtractCosignerKeys(intent.RegisterProofMessage));

                var topics =
                    vtxoTopics.Concat(cosignerTopics).ToHashSet();

                // If we have no topic to listen for, jump out.
                if (topics.Count is 0) return;

                var eventStreamRequest = new GetEventStreamRequest();
                eventStreamRequest.Topics.AddRange(topics);

                logger.LogInformation("Opening shared event stream with {TopicCount} topics for {IntentCount} intents",
                    topics.Count, _activeIntents.Count);

                _eventStreamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var streamCall =
                    arkServiceClient.GetEventStream(eventStreamRequest, cancellationToken: cancellationToken);

                await foreach (var eventResponse in streamCall.ResponseStream.ReadAllAsync(_eventStreamCts.Token))
                {
                    await ProcessEventForAllIntentsAsync(eventResponse, cancellationToken);
                }
            }
            catch (OperationCanceledException) 
            {
                logger.LogInformation("Stream was cancelled, possibly switching to new stream...");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                logger.LogInformation("Stream was cancelled, possibly switching to new stream...");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in shared event stream, restarting in {Seconds} seconds", EventStreamRetryDelay.TotalSeconds);
                await Task.Delay(EventStreamRetryDelay, cancellationToken);
            }
            finally
            {
                _eventStreamCts?.Dispose();
                _eventStreamCts = null;
            }
        }

        logger.LogInformation("Shared event stream stopped");
    }

    private async Task ProcessEventForAllIntentsAsync(GetEventStreamResponse eventResponse, CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // Handle BatchStarted event first - check all intents at once
        if (eventResponse.EventCase == GetEventStreamResponse.EventOneofCase.BatchStarted)
        {
            await HandleBatchStartedForAllIntentsAsync(eventResponse.BatchStarted, dbContext, cancellationToken);
        }

        // Process event for each active intent that might be affected
        foreach (var (intentId, intent) in _activeIntents.ToArray())
        {
            try
            {
                // If we have an active batch session, pass all events to it
                if (_activeBatchSessions.TryGetValue(intentId, out var batchSession))
                {
                    var isComplete = await batchSession.ProcessEventAsync(eventResponse, cancellationToken);
                    if (isComplete)
                    {
                        _activeBatchSessions.TryRemove(intentId, out _);
                        _activeIntents.TryRemove(intentId, out _);
                        TriggerStreamUpdate();
                        continue;
                    }
                }

                // Handle events that affect this intent
                switch (eventResponse.EventCase)
                {
                    case GetEventStreamResponse.EventOneofCase.BatchFailed:
                        if (eventResponse.BatchFailed.Id == intent.BatchId)
                        {
                            await HandleBatchFailedAsync(intent, eventResponse.BatchFailed, dbContext, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }
                        break;

                    case GetEventStreamResponse.EventOneofCase.BatchFinalized:
                        if (eventResponse.BatchFinalized.Id == intent.BatchId)
                        {
                            await HandleBatchFinalizedAsync(intent, eventResponse.BatchFinalized, dbContext, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing event for intent {IntentId}", intentId);
            }
        }
    }

    private async Task HandleBatchStartedForAllIntentsAsync(
        BatchStartedEvent batchEvent,
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Build a map of intent ID hashes to intent IDs for efficient lookup
        var intentHashMap = new Dictionary<string, string>();
        foreach (var (intentId, intent) in _activeIntents)
        {
            var intentIdBytes = Encoding.UTF8.GetBytes(intentId);
            var intentIdHash = Hashes.SHA256(intentIdBytes);
            var intentIdHashStr = Convert.ToHexString(intentIdHash).ToLowerInvariant();
            intentHashMap[intentIdHashStr] = intentId;
        }

        // Find all our intents that are included in this batch
        var selectedIntentIds = new List<string>();
        foreach (var intentIdHash in batchEvent.IntentIdHashes)
        {
            if (intentHashMap.TryGetValue(intentIdHash, out var intentId))
            {
                selectedIntentIds.Add(intentId);
            }
        }

        if (selectedIntentIds.Count == 0)
        {
            return; // None of our intents in this batch
        }

        logger.LogInformation("{Count} of our intents selected for batch {BatchId}: {IntentIds}",
            selectedIntentIds.Count, batchEvent.Id, string.Join(", ", selectedIntentIds));

        // Load all VTXOs and contracts for selected intents in one efficient query
        var walletIds = selectedIntentIds
            .Select(id => _activeIntents.TryGetValue(id, out var intent) ? intent.WalletId : null)
            .Where(wid => wid != null)
            .Select(wid => wid!)
            .Distinct()
            .ToList();
        
        if (walletIds.Count == 0)
        {
            logger.LogWarning("No valid wallet IDs found for selected intents");
            return;
        }
        
        // Collect all VTXO outpoints from all selected intents
        var allVtxoOutpoints = selectedIntentIds
            .Where(id => _activeIntents.ContainsKey(id))
            .SelectMany(id => _activeIntents[id].IntentVtxos
                .Select(iv => new OutPoint(uint256.Parse(iv.VtxoTransactionId), (uint)iv.VtxoTransactionOutputIndex)))
            .ToHashSet();
        
        // Get spendable coins for all wallets, filtered by the specific VTXOs locked in intents
        var walletCoins = await arkadeSpender.GetSpendableCoins(walletIds.ToArray(), allVtxoOutpoints, true, cancellationToken);
        var serverInfo = await serverInfoService.GetServerInfoAsync(cancellationToken);
        // Confirm registration and create batch sessions for all selected intents
        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent))
                continue;

            try
            {
                // Get signer
                var signer = await signerProvider.GetSigner(intent.WalletId, cancellationToken);
                if (signer == null)
                {
                    logger.LogError("Signer not available for wallet {WalletId}", intent.WalletId);
                    continue;
                }
                
                // Get spendable coins for this wallet from the pre-loaded data
                if (!walletCoins.TryGetValue(intent.WalletId, out var allWalletCoins))
                {
                    logger.LogError("No coins loaded for wallet {WalletId}", intent.WalletId);
                    continue;
                }
                
                // Filter to only the VTXOs locked by this intent
                var intentVtxoOutpoints = intent.IntentVtxos
                    .Select(iv => new OutPoint(uint256.Parse(iv.VtxoTransactionId), (uint)iv.VtxoTransactionOutputIndex))
                    .ToHashSet();
                
                var spendableCoins = allWalletCoins
                    .Where(coin => intentVtxoOutpoints.Contains(coin.Coin.Outpoint))
                    .ToList();
                
                if (spendableCoins.Count == 0)
                {
                    logger.LogError("No spendable coins found for intent {IntentId}", intentId);
                    continue;
                }
                
                // Confirm registration
                await arkServiceClient.ConfirmRegistrationAsync(
                    new ConfirmRegistrationRequest { IntentId = intentId },
                    cancellationToken: cancellationToken);

                intent.BatchId = batchEvent.Id;
                intent.State = ArkIntentState.BatchInProgress;
                intent.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Intent {IntentId} confirmed for batch {BatchId} and marked as in progress", intentId, batchEvent.Id);

                // Create and initialize batch session
                var session = new BatchSession(
                    clientTransport,
                    arkServiceClient,
                    arkTransactionBuilder,
                    serverInfo.Network,
                    signer,
                    intent,
                    spendableCoins.ToArray(),
                    batchEvent,
                    logger);
            
                await session.InitializeAsync(cancellationToken);
            
                // Store the session so events can be passed to it
                _activeBatchSessions[intent.IntentId!] = session;
            
                logger.LogInformation("Batch session initialized for intent {IntentId}", intent.InternalId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to confirm or create batch session for intent {IntentId}", intentId);
            }
        }
    }
    
    private static IEnumerable<string> ExtractCosignerKeys(string registerProofMessage)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Messages.RegisterIntentMessage>(registerProofMessage);
            return message?.CosignersPublicKeys ?? [];
        }
        catch (Exception)
        {
            // If we can't parse the message, return empty
            return [];
        }
    }

    private async Task HandleBatchFailedAsync(
        ArkIntent intent, 
        BatchFailedEvent batchEvent, 
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (intent.BatchId == batchEvent.Id)
        {
            logger.LogWarning("Batch {BatchId} failed for intent {IntentId}: {Reason}", 
                batchEvent.Id, intent.InternalId, batchEvent.Reason);
            
            intent.State = ArkIntentState.BatchFailed;
            intent.CancellationReason = $"Batch failed: {batchEvent.Reason}";
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleBatchFinalizedAsync(
        ArkIntent intent, 
        BatchFinalizedEvent finalizedEvent, 
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Batch finalized for intent {IntentId}, txid: {Txid}", 
            intent.InternalId, finalizedEvent.CommitmentTxid);

        intent.State = ArkIntentState.BatchSucceeded;
        intent.CommitmentTransactionId = finalizedEvent.CommitmentTxid;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    #endregion

    #region Intent Creation Helpers

    /// <summary>
    /// Creates register and delete intent PSBTs with BIP322 signatures.
    /// Replicates NNark's IntentGenerationService.CreateIntents logic.
    /// </summary>
    private static async Task<(PSBT RegisterTx, PSBT DeleteTx, string RegisterMessage, string DeleteMessage)> CreateIntentsAsync(
        Network network,
        ECPubKey[] cosigners,
        DateTimeOffset validAt,
        DateTimeOffset expireAt,
        IReadOnlyCollection<ArkPsbtSigner> inputSigners,
        IReadOnlyCollection<ArkTxOut>? outputs = null,
        CancellationToken cancellationToken = default)
    {
        var registerMsg = new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = outputs?.Select((x, i) => (x, i))
                .Where(o => o.x.Type == ArkTxOutType.Onchain)
                .Select((_, i) => i).ToArray() ?? [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = cosigners.Select(c => c.ToBytes().ToHexStringLower()).ToArray()
        };

        var deleteMsg = new Messages.DeleteIntentMessage
        {
            Type = "delete",
            ExpireAt = expireAt.ToUnixTimeSeconds()
        };

        var registerMessage = JsonSerializer.Serialize(registerMsg);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        var registerTx = await CreateIntentPsbtAsync(registerMessage, network, inputSigners.ToArray(), outputs?.Cast<TxOut>().ToArray(), cancellationToken);
        var deleteTx = await CreateIntentPsbtAsync(deleteMessage, network, inputSigners.ToArray(), null, cancellationToken);

        return (registerTx, deleteTx, registerMessage, deleteMessage);
    }

    /// <summary>
    /// Creates a BIP322-style PSBT for intent proof.
    /// </summary>
    private static async Task<PSBT> CreateIntentPsbtAsync(
        string message,
        Network network,
        ArkPsbtSigner[] inputs,
        IReadOnlyCollection<TxOut>? outputs,
        CancellationToken cancellationToken = default)
    {
        var firstInput = inputs.First();
        var toSignTx = CreateIntentBasePsbt(
            firstInput.Coin.ScriptPubKey,
            network,
            message,
            2U,
            0U,
            0U,
            inputs.Select(i => i.Coin).Cast<Coin>().ToArray());

        var toSignGTx = toSignTx.GetGlobalTransaction();
        if (outputs is not null && outputs.Count != 0)
        {
            toSignGTx.Outputs.RemoveAt(0);
            toSignGTx.Outputs.AddRange(outputs);
        }

        // Update first input coin with the to_spend transaction details
        var updatedInputs = new List<ArkPsbtSigner> { firstInput with { Coin = new ArkCoin(firstInput.Coin) } };
        updatedInputs.AddRange(inputs.Skip(1));
        updatedInputs[0].Coin.TxOut = toSignTx.Inputs[0].GetTxOut()!;
        updatedInputs[0].Coin.Outpoint = toSignTx.Inputs[0].PrevOut;

        var precomputedTransactionData = toSignGTx.PrecomputeTransactionData(updatedInputs.Select(i => i.Coin.TxOut).ToArray());

        toSignTx = PSBT.FromTransaction(toSignGTx, network).UpdateFrom(toSignTx);

        foreach (var signer in updatedInputs)
        {
            await signer.SignAndFillPsbt(toSignTx, precomputedTransactionData, cancellationToken: cancellationToken);
        }

        return toSignTx;
    }

    /// <summary>
    /// Creates the base PSBT structure for BIP322 intent proof.
    /// </summary>
    private static PSBT CreateIntentBasePsbt(
        Script pkScript,
        Network network,
        string message,
        uint version = 0, uint lockTime = 0, uint sequence = 0, Coin[]? fundProofOutputs = null)
    {
        var messageHash = CreateTaggedMessageHash("ark-intent-proof-message", message);

        // Create the "to_spend" transaction (BIP322 challenge)
        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF), new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, pkScript));
        var toSpendTxId = toSpend.GetHash();

        // Create the "to_sign" transaction (the proof)
        var toSign = network.CreateTransaction();
        toSign.Version = version;
        toSign.LockTime = lockTime;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpendTxId, 0))
        {
            Sequence = sequence
        });

        fundProofOutputs ??= [];

        foreach (var input in fundProofOutputs)
        {
            toSign.Inputs.Add(new TxIn(input.Outpoint, Script.Empty)
            {
                Sequence = sequence,
            });
        }
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));

        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(fundProofOutputs.Cast<ICoin>().ToArray());

        return psbt;
    }

    /// <summary>
    /// Creates a tagged SHA256 hash of a message (replicates NArk.Helpers.HashHelpers)
    /// </summary>
    private static byte[] CreateTaggedMessageHash(string tag, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged(tag);
        sha.Write(bytes);
        return sha.GetHash();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _serviceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
        
        _serviceCts?.Dispose();
        
        try
        {
            _eventStreamCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
        
        _eventStreamCts?.Dispose();
        _submissionTriggerTimer?.Dispose();
        
        _activeIntents.Clear();
        _activeBatchSessions.Clear();
        
        _disposed = true;
    }
}
