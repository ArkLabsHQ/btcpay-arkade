using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's IVtxoStorage interface.
/// Maps between plugin's VTXO entity and NNark's ArkVtxo record.
/// </summary>
public class EfCoreVtxoStorage : IVtxoStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;
    private readonly ISafetyService _safetyService;

    public event EventHandler<ArkVtxo>? VtxosChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreVtxoStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory, ISafetyService safetyService)
    {
        _dbContextFactory = dbContextFactory;
        _safetyService = safetyService;
    }

    public async Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        var outpointKey = $"vtxo-upsert::{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";

        // Lock to prevent race conditions during upsert
        await using var lockHandle = await _safetyService.LockKeyAsync(outpointKey, cancellationToken);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Vtxos.FirstOrDefaultAsync(
            v => v.TransactionId == vtxo.TransactionId &&
                 v.TransactionOutputIndex == (int)vtxo.TransactionOutputIndex,
            cancellationToken);

        var isNew = existing == null;
        var entity = existing ?? new VTXO();

        entity.TransactionId = vtxo.TransactionId;
        entity.TransactionOutputIndex = (int)vtxo.TransactionOutputIndex;
        entity.Script = vtxo.Script;
        entity.Amount = (long)vtxo.Amount;
        entity.SpentByTransactionId = vtxo.SpentByTransactionId;
        entity.SettledByTransactionId = vtxo.SettledByTransactionId;
        // this is messy, and data in db is gonna be inconsistent (migration path), but the end result should be be safe.
        entity.Recoverable = vtxo.Swept;
        entity.SeenAt = vtxo.CreatedAt;
        entity.ExpiresAt = vtxo.ExpiresAt ?? DateTimeOffset.MaxValue;

        if (isNew)
        {
            await db.Vtxos.AddAsync(entity, cancellationToken);
        }

        if (await db.SaveChangesAsync(cancellationToken) > 0)
        {
            // Raise event for direct subscribers (e.g., ArkPayoutHandler, ArkContractInvoiceListener)
            VtxosChanged?.Invoke(this, vtxo);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

        return isNew;
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
        IReadOnlyCollection<string>? scripts = null,
        IReadOnlyCollection<OutPoint>? outpoints = null,
        string[]? walletIds = null,
        bool includeSpent = false,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Vtxos.AsQueryable();

        // Filter by scripts
        if (scripts is { })
        {
            var scriptSet = scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (outpoints is {  })
        {
            var outpointPairs = outpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();
            query = query.Where(v => outpointPairs.Contains(v.TransactionId + v.TransactionOutputIndex));
        }

        // Filter by wallet IDs (join with WalletContracts)
        if (walletIds is {  })
        {
            var walletScripts = db.WalletContracts
                .Where(c => walletIds.Contains(c.WalletId))
                .Select(c => c.Script);
            query = query.Where(v => walletScripts.Contains(v.Script));
        }

        // Filter by spent state
        if (!includeSpent)
        {
            query = query.Where(v =>
                (v.SpentByTransactionId ?? "").Length == 0 &&
                (v.SettledByTransactionId ?? "").Length == 0);
        }

        // Search text filter - search in TransactionId, Script, and also match against Contract Type
        if (!string.IsNullOrEmpty(searchText))
        {
            // Get contract scripts that match by Type
            var matchingContractScripts = db.WalletContracts
                .Where(c => c.Type.Contains(searchText))
                .Select(c => c.Script);

            query = query.Where(v =>
                v.TransactionId.Contains(searchText) ||
                v.Script.Contains(searchText) ||
                matchingContractScripts.Contains(v.Script));
        }

        // Order by creation date (newest first) for consistent pagination
        query = query.OrderByDescending(v => v.SeenAt);

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
        return entities.Select(MapToArkVtxo).ToList();
    }

    private static ArkVtxo MapToArkVtxo(VTXO entity)
    {
        return new ArkVtxo(
            Script: entity.Script,
            TransactionId: entity.TransactionId,
            TransactionOutputIndex: (uint)entity.TransactionOutputIndex,
            Amount: (ulong)entity.Amount,
            SpentByTransactionId: entity.SpentByTransactionId,
            SettledByTransactionId: entity.SettledByTransactionId,
            Swept: entity.Recoverable,
            CreatedAt: entity.SeenAt,
            ExpiresAt: entity.ExpiresAt == DateTimeOffset.MaxValue ? null : entity.ExpiresAt,
            ExpiresAtHeight: null // Plugin doesn't track height-based expiry
        );
    }
}
