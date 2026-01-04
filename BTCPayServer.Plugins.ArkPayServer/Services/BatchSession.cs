using System.Text.Json;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Abstractions.Wallets;
using NArk.Batches;
using NArk.Extensions;
using NArk.Helpers;
using NArk.Models;
using NArk.Scripts;
using NArk.Transactions;
using NArk.Transport;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Handles participation in a batch settlement round for a specific intent
/// </summary>
public class BatchSession
{
    private readonly IClientTransport _clientTransport;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly TransactionHelpers.ArkTransactionBuilder _arkTransactionBuilder;
    private readonly Network _network;
    private readonly ISigningEntity _signer;
    private readonly ArkIntent _arkIntent;
    private readonly ArkPsbtSigner[] _ins;
    private readonly BatchStartedEvent _batchStartedEvent;
    private readonly string _intentId;
    private readonly string _batchId;
    private readonly ILogger _logger;

    public BatchSession(
        IClientTransport clientTransport,
        ArkService.ArkServiceClient arkServiceClient,
        TransactionHelpers.ArkTransactionBuilder arkTransactionBuilder,
        Network network,
        ISigningEntity signer,
        ArkIntent arkIntent,
        ArkPsbtSigner[] ins,
        BatchStartedEvent batchStartedEvent,
        ILogger logger)
    {
        _clientTransport = clientTransport;
        _arkServiceClient = arkServiceClient;
        _arkTransactionBuilder = arkTransactionBuilder;
        _network = network;
        _signer = signer;
        _arkIntent = arkIntent;
        _ins = ins;
        _batchStartedEvent = batchStartedEvent;
        _logger = logger;
        _intentId = arkIntent.IntentId!;
        _batchId = batchStartedEvent.Id;

        IntentParameters = JsonSerializer.Deserialize<Messages.RegisterIntentMessage>(arkIntent.RegisterProofMessage);
    }

    public Messages.RegisterIntentMessage? IntentParameters { get; }

    private TreeSignerSession? _signingSession;
    private readonly List<TxTreeNode> _vtxoChunks = new();
    private readonly List<TxTreeNode> _connectorsChunks = new();
    private uint256? _sweepTapTreeRoot;
    private bool _isComplete;

    /// <summary>
    /// Initialize the batch session (call this before processing events)
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing batch session for intent {IntentId} in batch {BatchId}", _intentId, _batchId);

        // Get server info to build sweep tap tree
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var batchExpiry = new Sequence((uint)_batchStartedEvent.BatchExpiry);
        var sweepTapScript = new UnilateralPathArkTapScript(batchExpiry, new NofNMultisigTapScript([serverInfo.ForfeitPubKey]));
        _sweepTapTreeRoot = sweepTapScript.Build().LeafHash;
    }


    /// <summary>
    /// Process a single event from the event stream
    /// </summary>
    /// <returns>True if the batch session is complete, false otherwise</returns>
    public async Task<bool> ProcessEventAsync(GetEventStreamResponse eventResponse, CancellationToken cancellationToken = default)
    {
        if (_isComplete)
            return true;

        if (_sweepTapTreeRoot == null)
            throw new InvalidOperationException("Batch session not initialized. Call InitializeAsync first.");

        try
        {
            switch (eventResponse.EventCase)
            {
                case GetEventStreamResponse.EventOneofCase.TreeTx:
                    HandleTreeTxEvent(eventResponse.TreeTx, _vtxoChunks, _connectorsChunks);
                    break;

                case GetEventStreamResponse.EventOneofCase.TreeSigningStarted:
                    if (_vtxoChunks.Count > 0)
                    {
                        _signingSession = await HandleTreeSigningStartedAsync(
                            eventResponse.TreeSigningStarted,
                            _sweepTapTreeRoot,
                            _vtxoChunks,
                            cancellationToken);
                    }
                    break;

                case GetEventStreamResponse.EventOneofCase.TreeNonces:
                    if (_signingSession != null)
                    {
                        var val = eventResponse.TreeNonces.Nonces.Values.Select(s =>
                            new MusigPubNonce(Encoders.Hex.DecodeData(s)));
                        var txid = uint256.Parse(eventResponse.TreeNonces.Txid)!;
                        await _signingSession.AggregateNonces(txid, val.ToArray(), cancellationToken);


                    }

                    break;
                case GetEventStreamResponse.EventOneofCase.TreeNoncesAggregated:
                    if (_signingSession != null)
                    {
                        await HandleAggregatedTreeNoncesEventAsync(
                            eventResponse.TreeNoncesAggregated,
                            _signingSession,
                            cancellationToken);
                    }
                    break;

                case GetEventStreamResponse.EventOneofCase.BatchFinalization:
                    await HandleBatchFinalizationAsync(
                        eventResponse.BatchFinalization,
                        _connectorsChunks,
                        cancellationToken);
                    break;

                case GetEventStreamResponse.EventOneofCase.BatchFinalized:
                    if (eventResponse.BatchFinalized.Id == _batchId)
                    {
                        _logger.LogInformation("Batch {BatchId} finalized successfully", _batchId);
                        _isComplete = true;
                        return true;
                    }
                    break;

                case GetEventStreamResponse.EventOneofCase.BatchFailed:
                    if (eventResponse.BatchFailed.Id == _batchId)
                    {
                        _logger.LogWarning("Batch {BatchId} failed: {Reason}", _batchId, eventResponse.BatchFailed.Reason);
                        _isComplete = true;
                        throw new InvalidOperationException($"Batch failed: {eventResponse.BatchFailed.Reason}");
                    }
                    break;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event for batch session {IntentId}", _intentId);
            _isComplete = true;
            throw;
        }
    }

    /// <summary>
    /// Whether the batch session has completed (successfully or with failure)
    /// </summary>
    public bool IsComplete => _isComplete;

    private void HandleTreeTxEvent(TreeTxEvent treeTxEvent, List<TxTreeNode> vtxoChunks, List<TxTreeNode> connectorsChunks)
    {
        var txNode = new TxTreeNode(
            PSBT.Parse(treeTxEvent.Tx, _network),
            treeTxEvent.Children.ToDictionary(
                kvp => (int)kvp.Key,
                kvp => uint256.Parse(kvp.Value)));

        if (treeTxEvent.BatchIndex == 0)
        {
            vtxoChunks.Add(txNode);
            _logger.LogDebug("Received VTXO tree chunk");
        }
        else if (treeTxEvent.BatchIndex == 1)
        {
            connectorsChunks.Add(txNode);
            _logger.LogDebug("Received connector tree chunk");
        }
        else
        {
            _logger.LogWarning("Unknown batch index: {BatchIndex}", treeTxEvent.BatchIndex);
        }
    }

    private async Task<TreeSignerSession> HandleTreeSigningStartedAsync(
        TreeSigningStartedEvent signingEvent,
        uint256 sweepTapTreeRoot,
        List<TxTreeNode> vtxoChunks,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tree signing started for batch {BatchId}", _batchId);

        // Build VTXO tree from chunks
        var vtxoGraph = TxTree.Create(vtxoChunks);

        // Validate the tree
        var commitmentTx = PSBT.Parse(signingEvent.UnsignedCommitmentTx, _network);
        TreeValidator.ValidateVtxoTxGraph(vtxoGraph, commitmentTx, sweepTapTreeRoot);

        // Validate that all intent outputs exist in the correct locations
        ValidateIntentOutputs(vtxoGraph, commitmentTx);

        // Get shared output amount
        var sharedOutput = commitmentTx.Outputs[0];
        if (sharedOutput?.Value == null)
            throw new InvalidOperationException("Shared output not found in commitment transaction");

        // Create signing session
        var session = new TreeSignerSession(_signer, vtxoGraph, sweepTapTreeRoot, sharedOutput.Value);

        // Generate and submit nonces
        var nonces = await session.GetNoncesAsync(cancellationToken);
        var pubKey = OutputDescriptorHelpers.Extract(await _signer.GetOutputDescriptor(cancellationToken)).XOnlyPubKey;

        var request = new SubmitTreeNoncesRequest
        {
            BatchId = signingEvent.Id,
            Pubkey = Convert.ToHexString(pubKey.ToBytes()).ToLowerInvariant()
        };

        request.TreeNonces.Add(nonces.ToDictionary(pair => pair.Key.ToString(), pair => Convert.ToHexString(pair.Value.ToBytes()).ToLowerInvariant()));
        await _arkServiceClient.SubmitTreeNoncesAsync(
            request,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Submitted tree nonces for batch {BatchId}", _batchId);
        return session;
    }

    private async Task HandleAggregatedTreeNoncesEventAsync(
        TreeNoncesAggregatedEvent aggregatedEvent,
        TreeSignerSession session,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tree nonces aggregated for batch {BatchId}", _batchId);


        // Process nonces in the session
        session.VerifyAggregatedNonces(
            aggregatedEvent.TreeNonces.ToDictionary(pair => uint256.Parse(pair.Key), pair => new MusigPubNonce(Encoders.Hex.DecodeData(pair.Value))), cancellationToken);

        // Sign and submit signatures
        var signatures = await session.SignAsync(cancellationToken);

        var sigPubKey = OutputDescriptorHelpers.Extract(await _signer.GetOutputDescriptor(cancellationToken)).XOnlyPubKey;

        var request = new SubmitTreeSignaturesRequest
        {
            BatchId = _batchId,
            Pubkey = Convert.ToHexString(sigPubKey.ToBytes()).ToLowerInvariant()
        };

        request.TreeSignatures.Add(signatures.ToDictionary(pair => pair.Key.ToString(), pair => Convert.ToHexString(pair.Value.ToBytes()).ToLowerInvariant()));
        await _arkServiceClient.SubmitTreeSignaturesAsync(
            request,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Submitted tree signatures for batch {BatchId}", _batchId);
    }

    private async Task HandleBatchFinalizationAsync(
        BatchFinalizationEvent finalizationEvent,
        List<TxTreeNode> connectorsChunks,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Batch finalization for batch {BatchId}", _batchId);

        // Build and validate connectors graph if present
        TxTree? connectorsGraph = null;
        if (connectorsChunks.Count > 0)
        {
            connectorsGraph = TxTree.Create(connectorsChunks);
            var commitmentPsbt = PSBT.Parse(finalizationEvent.CommitmentTx, _network);
            TreeValidator.ValidateConnectorsTxGraph(commitmentPsbt, connectorsGraph);
            _logger.LogDebug("Connector tree validated");
        }

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var signedForfeits = new List<string>();

        // Get connector leaves for forfeit transactions
        var connectorsLeaves = connectorsGraph?.Leaves().ToList() ?? new List<PSBT>();
        int connectorIndex = 0;

        foreach (var vtxoCoin in _ins)
        {
            // Skip recoverable coins (notes) - they don't need forfeit transactions
            if (vtxoCoin.Coin.Recoverable)
            {
                _logger.LogDebug("Skipping recoverable coin {Outpoint}", vtxoCoin.Coin.Outpoint);
                continue;
            }

            // Check if we have enough connectors
            if (connectorsLeaves.Count == 0)
            {
                throw new InvalidOperationException("Connectors not received from operator");
            }

            if (connectorIndex >= connectorsLeaves.Count)
            {
                throw new InvalidOperationException(
                    $"Not enough connectors received. Need at least {connectorIndex + 1}, got {connectorsLeaves.Count}");
            }

            // Get the next connector leaf
            var connectorLeaf = connectorsLeaves[connectorIndex];
            var connectorOutput = connectorLeaf.Outputs.FirstOrDefault();

            if (connectorOutput == null)
            {
                throw new InvalidOperationException($"Connector leaf at index {connectorIndex} has no outputs");
            }

            // Create connector coin from the leaf transaction
            var connectorTxId = connectorLeaf.GetGlobalTransaction().GetHash()!;
            var connectorCoin = new Coin(
                new OutPoint(connectorTxId, 0),
                connectorLeaf.Outputs[0].GetTxOut());

            connectorIndex++;

            _logger.LogDebug("Constructing forfeit tx for VTXO {Outpoint} with connector {ConnectorOutpoint}",
                vtxoCoin.Coin.Outpoint, connectorCoin.Outpoint);

            // Construct and sign forfeit transaction using ArkTransactionBuilder
            var forfeitTx = await _arkTransactionBuilder.ConstructForfeitTx(
                serverInfo,
                vtxoCoin,
                connectorCoin,
                serverInfo.ForfeitAddress,
                cancellationToken);

            signedForfeits.Add(forfeitTx.ToBase64());
            _logger.LogDebug("Forfeit tx constructed for VTXO {Outpoint}", vtxoCoin.Coin.Outpoint);
        }

        // Submit all signed forfeit transactions
        if (signedForfeits.Count > 0)
        {
            _logger.LogInformation("Submitting {Count} signed forfeit transactions", signedForfeits.Count);
            await _arkServiceClient.SubmitSignedForfeitTxsAsync(new SubmitSignedForfeitTxsRequest
            {
                SignedForfeitTxs = { signedForfeits }
            }, cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully submitted forfeit transactions for batch {BatchId}", _batchId);
        }
        else
        {
            _logger.LogDebug("No forfeit transactions to submit (all coins are recoverable)");
        }
    }

    /// <summary>
    /// Validates that all outputs specified in the intent exist in the correct locations:
    /// - Onchain outputs must exist in the commitment transaction
    /// - Offchain outputs (VTXOs) must exist as leaves in the VTXO tree
    /// </summary>
    private void ValidateIntentOutputs(TxTree vtxoGraph, PSBT commitmentTx)
    {
        if (IntentParameters == null)
        {
            _logger.LogWarning("Intent parameters not available, skipping output validation");
            return;
        }

        // Parse the intent to get the outputs
        var intentOutputs = ParseIntentOutputs();
        if (intentOutputs.Count == 0)
        {
            _logger.LogDebug("No outputs to validate in intent");
            return;
        }

        var onchainIndexes = new HashSet<int>(IntentParameters.OnchainOutputsIndexes ?? []);

        // Get all VTXO leaf outputs for validation
        var vtxoLeaves = vtxoGraph.Leaves().ToList();
        var vtxoLeafOutputs = vtxoLeaves
            .SelectMany(leaf => leaf.GetGlobalTransaction().Outputs
                .Select((output, idx) => new { Output = output, Tx = leaf, Index = idx }))
            .ToList();

        for (int i = 0; i < intentOutputs.Count; i++)
        {
            var output = intentOutputs[i];
            var isOnchain = onchainIndexes.Contains(i);

            if (isOnchain)
            {
                // Validate onchain output exists in commitment transaction
                var found = commitmentTx.Outputs.Any(txOut =>
                    txOut.ScriptPubKey == output.ScriptPubKey &&
                    txOut.Value == output.Value);

                if (!found)
                {
                    throw new InvalidOperationException(
                        $"Onchain output {i} not found in commitment transaction. " +
                        $"Expected: {output.Value} sats to {output.ScriptPubKey}");
                }

                _logger.LogDebug("Validated onchain output {Index}: {Amount} sats", i, output.Value.Satoshi);
            }
            else
            {
                // Validate offchain output exists as a leaf in the VTXO tree
                var found = vtxoLeafOutputs.Any(leafOutput =>
                    leafOutput.Output.ScriptPubKey == output.ScriptPubKey &&
                    leafOutput.Output.Value == output.Value);

                if (!found)
                {
                    throw new InvalidOperationException(
                        $"Offchain output {i} not found in VTXO tree leaves. " +
                        $"Expected: {output.Value} sats to {output.ScriptPubKey}");
                }

                _logger.LogDebug("Validated offchain output {Index}: {Amount} sats", i, output.Value.Satoshi);
            }
        }

        _logger.LogInformation("Successfully validated all {Count} intent outputs", intentOutputs.Count);
    }

    /// <summary>
    /// Parses the intent outputs from the register proof transaction.
    /// The outputs are embedded directly in the BIP322 signature transaction
    /// (deviation from standard BIP322 to declare outputs).
    /// </summary>
    private List<TxOut> ParseIntentOutputs()
    {
        try
        {
            var registerProof = PSBT.Parse(_arkIntent.RegisterProof, _network);

            // The register proof transaction contains the outputs directly
            // (see IntentUtils.CreateIntent - outputs are added to the BIP322 tx)
            // Skip the first input which is the BIP322 "to_spend" input
            var outputs = registerProof.Outputs.ToList();

            _logger.LogDebug("Parsed {Count} outputs from register proof transaction", outputs.Count);
            return registerProof.GetGlobalTransaction().Outputs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse intent outputs from register proof");
            return new List<TxOut>();
        }
    }
}
