using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
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

    public event EventHandler<ArkVtxo>? VtxosChanged;

    public EfCoreVtxoStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
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
        entity.Recoverable = vtxo.Recoverable;
        entity.SeenAt = vtxo.CreatedAt;
        entity.ExpiresAt = vtxo.ExpiresAt ?? DateTimeOffset.MaxValue;

        if (isNew)
        {
            db.Vtxos.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        VtxosChanged?.Invoke(this, vtxo);

        return isNew;
    }

    public async Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var txId = outpoint.Hash.ToString();
        var index = (int)outpoint.N;

        var entity = await db.Vtxos.FirstOrDefaultAsync(
            v => v.TransactionId == txId && v.TransactionOutputIndex == index,
            cancellationToken);

        return entity == null ? null : MapToArkVtxo(entity);
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(
        IReadOnlyCollection<string> scripts,
        bool allowSpent = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = scripts.ToHashSet();
        var query = db.Vtxos.Where(v => scriptSet.Contains(v.Script));

        if (!allowSpent)
        {
            query = query.Where(v =>
                v.SpentByTransactionId == null &&
                v.SettledByTransactionId == null);
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToArkVtxo).ToList();
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Vtxos
            .Where(v => v.SpentByTransactionId == null && v.SettledByTransactionId == null)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToArkVtxo).ToList();
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Vtxos.ToListAsync(cancellationToken);
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
            Recoverable: entity.Recoverable,
            CreatedAt: entity.SeenAt,
            ExpiresAt: entity.ExpiresAt == DateTimeOffset.MaxValue ? null : entity.ExpiresAt,
            ExpiresAtHeight: null // Plugin doesn't track height-based expiry
        );
    }
}
