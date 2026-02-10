using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Intents;
using NBitcoin;
using PluginArkIntent = BTCPayServer.Plugins.ArkPayServer.Data.ArkIntent;
using NNarkArkIntent = NArk.Abstractions.Intents.ArkIntent;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's IIntentStorage interface.
/// Maps between plugin's ArkIntent entity and NNark's ArkIntent record.
/// </summary>
public class EfCoreIntentStorage : IIntentStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;
    private readonly ILogger<EfCoreIntentStorage>? _logger;

    public event EventHandler<NNarkArkIntent>? IntentChanged;

    public EfCoreIntentStorage(
        IDbContextFactory<ArkPluginDbContext> dbContextFactory,
        ILogger<EfCoreIntentStorage>? logger = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task SaveIntent(string walletId, NNarkArkIntent intent, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Try to find existing by IntentTxId
        var existing = await db.Intents
            .Include(i => i.IntentVtxos)
            .FirstOrDefaultAsync(i => i.IntentTxId == intent.IntentTxId, cancellationToken);

        if (existing != null)
        {
            // Update existing
            existing.IntentId = intent.IntentId;
            existing.WalletId = intent.WalletId;
            existing.State = intent.State;
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
                IntentTxId = intent.IntentTxId,
                IntentId = intent.IntentId,
                WalletId = intent.WalletId,
                State = intent.State,
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
        string[]? walletIds = null,
        string[]? intentTxIds = null,
        string[]? intentIds = null,
        OutPoint[]? containingInputs = null,
        ArkIntentState[]? states = null,
        DateTimeOffset? validAt = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Intents
            .Include(i => i.IntentVtxos)
            .AsQueryable();

        // Filter by wallet IDs
        if (walletIds is {  })
        {
            query = query.Where(i => walletIds.Contains(i.WalletId));
        }

        // Filter by intent transaction IDs
        if (intentTxIds is {  })
        {
            query = query.Where(i => intentTxIds.Contains(i.IntentTxId));
        }

        // Filter by intent IDs
        if (intentIds is {  })
        {
            query = query.Where(i => i.IntentId != null && intentIds.Contains(i.IntentId));
        }

        // Filter by states
        if (states is {  })
        {
            query = query.Where(i => states.Contains(i.State));
        }

        // Filter by validity time (null ValidFrom/ValidUntil means always valid)
        if (validAt.HasValue)
        {
            query = query.Where(i =>
                (i.ValidFrom == null || i.ValidFrom <= validAt.Value) &&
                (i.ValidUntil == null || i.ValidUntil >= validAt.Value));
        }

        // Order by creation date for consistent pagination
        query = query.OrderByDescending(i => i.CreatedAt);

        // Pagination
        if (skip.HasValue)
        {
            query = query.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            query = query.Take(take.Value);
        }

        var entities = await query.AsNoTracking().ToListAsync(cancellationToken);

        // Filter by containing inputs in memory (EF Core can't easily do this join)
        if (containingInputs is {  })
        {
            var inputStrings = containingInputs.Select(op =>
                $"{op.Hash}:{op.N}").ToHashSet();

            entities = entities.Where(e =>
                e.IntentVtxos.Any(iv =>
                    inputStrings.Contains($"{iv.VtxoTransactionId}:{iv.VtxoTransactionOutputIndex}")))
                .ToList();
        }

        return entities.Select(MapToNNarkIntent).ToList();
    }

    public async Task<IReadOnlyCollection<OutPoint>> GetLockedVtxoOutpoints(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var results = await db.IntentVtxos
            .Include(iv => iv.Intent)
            .Include(iv => iv.Vtxo)
            .Where(iv => iv.Intent.WalletId == walletId &&
                        (iv.Intent.State == ArkIntentState.WaitingToSubmit ||
                         iv.Intent.State == ArkIntentState.WaitingForBatch))
            .Select(iv => new { iv.Vtxo!.TransactionId, iv.Vtxo.TransactionOutputIndex })
            .ToListAsync(cancellationToken);

        return results
            .Select(r => new OutPoint(new uint256(r.TransactionId), (uint)r.TransactionOutputIndex))
            .ToList();
    }

    private NNarkArkIntent MapToNNarkIntent(PluginArkIntent entity)
    {
        return new NNarkArkIntent(
            IntentTxId: entity.IntentTxId,
            IntentId: entity.IntentId,
            WalletId: entity.WalletId,
            State: entity.State,
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

    #region Plugin-specific methods (for views that need plugin entities)

    /// <summary>
    /// Gets intent VTXOs grouped by IntentTxId.
    /// Used by Intents view to display VTXOs associated with each intent.
    /// </summary>
    public async Task<Dictionary<string, ArkIntentVtxo[]>> GetIntentVtxosByIntentTxIdsAsync(
        IEnumerable<string> intentTxIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var idSet = intentTxIds.ToHashSet();
        var vtxos = await db.IntentVtxos
            .Include(iv => iv.Vtxo)
            .Where(iv => idSet.Contains(iv.IntentTxId))
            .ToArrayAsync(cancellationToken);

        return vtxos
            .GroupBy(iv => iv.IntentTxId)
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>
    /// Gets intents with pagination and optional filtering.
    /// Returns plugin entities for use in views that display intent details.
    /// </summary>
    public async Task<IReadOnlyList<PluginArkIntent>> GetIntentsWithPaginationAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string? searchText = null,
        ArkIntentState? state = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Intents.Where(i => i.WalletId == walletId);

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(i =>
                (i.IntentId != null && i.IntentId.Contains(searchText)) ||
                (i.BatchId != null && i.BatchId.Contains(searchText)) ||
                (i.CommitmentTransactionId != null && i.CommitmentTransactionId.Contains(searchText)));
        }

        if (state.HasValue)
        {
            query = query.Where(i => i.State == state.Value);
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    #endregion
}
