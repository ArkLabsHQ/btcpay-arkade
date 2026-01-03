using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Intents;
using NBitcoin;
using PluginArkIntent = BTCPayServer.Plugins.ArkPayServer.Data.ArkIntent;
using PluginArkIntentState = BTCPayServer.Plugins.ArkPayServer.Data.ArkIntentState;
using NNarkArkIntent = NArk.Abstractions.Intents.ArkIntent;
using NNarkArkIntentState = NArk.Abstractions.Intents.ArkIntentState;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's IIntentStorage interface.
/// Maps between plugin's ArkIntent entity and NNark's ArkIntent record.
/// </summary>
public class EfCoreIntentStorage : IIntentStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    public event EventHandler<NNarkArkIntent>? IntentChanged;

    public EfCoreIntentStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task SaveIntent(string walletId, NNarkArkIntent intent, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Try to find existing by InternalId (using Guid to int mapping)
        var internalId = GuidToInt(intent.InternalId);
        var existing = await db.Intents
            .Include(i => i.IntentVtxos)
            .FirstOrDefaultAsync(i => i.InternalId == internalId, cancellationToken);

        if (existing != null)
        {
            // Update existing
            existing.IntentId = intent.IntentId;
            existing.WalletId = intent.WalletId;
            existing.State = MapState(intent.State);
            existing.ValidFrom = intent.ValidFrom;
            existing.ValidUntil = intent.ValidUntil;
            existing.UpdatedAt = intent.UpdatedAt;
            existing.RegisterProof = intent.RegisterProof;
            existing.RegisterProofMessage = intent.RegisterProofMessage;
            existing.DeleteProof = intent.DeleteProof;
            existing.DeleteProofMessage = intent.DeleteProofMessage;
            existing.BatchId = intent.BatchId;
            existing.CommitmentTransactionId = intent.CommitmentTransactionId;
            existing.CancellationReason = intent.CancellationReason;
            existing.SignerDescriptor = intent.SignerDescriptor;
        }
        else
        {
            var entity = new PluginArkIntent
            {
                IntentId = intent.IntentId,
                WalletId = intent.WalletId,
                State = MapState(intent.State),
                ValidFrom = intent.ValidFrom,
                ValidUntil = intent.ValidUntil,
                CreatedAt = intent.CreatedAt,
                UpdatedAt = intent.UpdatedAt,
                RegisterProof = intent.RegisterProof,
                RegisterProofMessage = intent.RegisterProofMessage,
                DeleteProof = intent.DeleteProof,
                DeleteProofMessage = intent.DeleteProofMessage,
                BatchId = intent.BatchId,
                CommitmentTransactionId = intent.CommitmentTransactionId,
                CancellationReason = intent.CancellationReason,
                SignerDescriptor = intent.SignerDescriptor,
                IntentVtxos = intent.IntentVtxos.Select(op => new ArkIntentVtxo
                {
                    VtxoTransactionId = op.Hash.ToString(),
                    VtxoTransactionOutputIndex = (int)op.N,
                    LinkedAt = DateTimeOffset.UtcNow
                }).ToList()
            };
            db.Intents.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        IntentChanged?.Invoke(this, intent);
    }

    public async Task<IReadOnlyCollection<NNarkArkIntent>> GetIntents(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Intents
            .Include(i => i.IntentVtxos)
            .Where(i => i.WalletId == walletId)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToNNarkIntent).ToList();
    }

    public async Task<NNarkArkIntent?> GetIntentByInternalId(
        Guid internalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var id = GuidToInt(internalId);
        var entity = await db.Intents
            .Include(i => i.IntentVtxos)
            .FirstOrDefaultAsync(i => i.InternalId == id, cancellationToken);

        return entity == null ? null : MapToNNarkIntent(entity);
    }

    public async Task<NNarkArkIntent?> GetIntentByIntentId(
        string walletId,
        string intentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.Intents
            .Include(i => i.IntentVtxos)
            .FirstOrDefaultAsync(i => i.WalletId == walletId && i.IntentId == intentId,
                cancellationToken);

        return entity == null ? null : MapToNNarkIntent(entity);
    }

    public async Task<IReadOnlyCollection<NNarkArkIntent>> GetIntentsByInputs(
        string walletId,
        OutPoint[] inputs,
        bool pendingOnly = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var inputStrings = inputs.Select(op =>
            $"{op.Hash}:{op.N}").ToHashSet();

        var query = db.Intents
            .Include(i => i.IntentVtxos)
            .Where(i => i.WalletId == walletId);

        if (pendingOnly)
        {
            query = query.Where(i =>
                i.State == PluginArkIntentState.WaitingToSubmit ||
                i.State == PluginArkIntentState.WaitingForBatch ||
                i.State == PluginArkIntentState.BatchInProgress);
        }

        var entities = await query.ToListAsync(cancellationToken);

        // Filter by inputs in memory (EF Core can't easily do this join)
        var filtered = entities.Where(e =>
            e.IntentVtxos.Any(iv =>
                inputStrings.Contains($"{iv.VtxoTransactionId}:{iv.VtxoTransactionOutputIndex}")));

        return filtered.Select(MapToNNarkIntent).ToList();
    }

    public async Task<IReadOnlyCollection<NNarkArkIntent>> GetUnsubmittedIntents(
        DateTimeOffset? validAt = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Intents
            .Include(i => i.IntentVtxos)
            .Where(i => i.State == PluginArkIntentState.WaitingToSubmit);

        if (validAt.HasValue)
        {
            query = query.Where(i =>
                i.ValidFrom <= validAt.Value && i.ValidUntil >= validAt.Value);
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToNNarkIntent).ToList();
    }

    public async Task<IReadOnlyCollection<NNarkArkIntent>> GetActiveIntents(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Intents
            .Include(i => i.IntentVtxos)
            .Where(i =>
                i.State == PluginArkIntentState.WaitingToSubmit ||
                i.State == PluginArkIntentState.WaitingForBatch ||
                i.State == PluginArkIntentState.BatchInProgress)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToNNarkIntent).ToList();
    }

    private NNarkArkIntent MapToNNarkIntent(PluginArkIntent entity)
    {
        return new NNarkArkIntent(
            InternalId: IntToGuid(entity.InternalId),
            IntentId: entity.IntentId,
            WalletId: entity.WalletId,
            State: MapState(entity.State),
            ValidFrom: entity.ValidFrom,
            ValidUntil: entity.ValidUntil,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            RegisterProof: entity.RegisterProof,
            RegisterProofMessage: entity.RegisterProofMessage,
            DeleteProof: entity.DeleteProof,
            DeleteProofMessage: entity.DeleteProofMessage,
            BatchId: entity.BatchId,
            CommitmentTransactionId: entity.CommitmentTransactionId,
            CancellationReason: entity.CancellationReason,
            IntentVtxos: entity.IntentVtxos?.Select(iv =>
                new OutPoint(new uint256(iv.VtxoTransactionId), (uint)iv.VtxoTransactionOutputIndex)
            ).ToArray() ?? [],
            SignerDescriptor: entity.SignerDescriptor ?? ""
        );
    }

    private static NNarkArkIntentState MapState(PluginArkIntentState state) => state switch
    {
        PluginArkIntentState.WaitingToSubmit => NNarkArkIntentState.WaitingToSubmit,
        PluginArkIntentState.WaitingForBatch => NNarkArkIntentState.WaitingForBatch,
        PluginArkIntentState.BatchInProgress => NNarkArkIntentState.BatchInProgress,
        PluginArkIntentState.BatchFailed => NNarkArkIntentState.BatchFailed,
        PluginArkIntentState.BatchSucceeded => NNarkArkIntentState.BatchSucceeded,
        PluginArkIntentState.Cancelled => NNarkArkIntentState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static PluginArkIntentState MapState(NNarkArkIntentState state) => state switch
    {
        NNarkArkIntentState.WaitingToSubmit => PluginArkIntentState.WaitingToSubmit,
        NNarkArkIntentState.WaitingForBatch => PluginArkIntentState.WaitingForBatch,
        NNarkArkIntentState.BatchInProgress => PluginArkIntentState.BatchInProgress,
        NNarkArkIntentState.BatchFailed => PluginArkIntentState.BatchFailed,
        NNarkArkIntentState.BatchSucceeded => PluginArkIntentState.BatchSucceeded,
        NNarkArkIntentState.Cancelled => PluginArkIntentState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    /// <summary>
    /// Convert int InternalId to Guid for NNark compatibility.
    /// Uses a deterministic mapping.
    /// </summary>
    private static Guid IntToGuid(int id)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(id).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    /// <summary>
    /// Convert Guid back to int InternalId.
    /// </summary>
    private static int GuidToInt(Guid guid)
    {
        var bytes = guid.ToByteArray();
        return BitConverter.ToInt32(bytes, 0);
    }
}
