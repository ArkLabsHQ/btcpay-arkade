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
    public event EventHandler? ActiveScriptsChanged;

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

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Vtxos.AsQueryable();

        // Filter by scripts
        if (filter.Scripts is { Count: > 0 })
        {
            var scriptSet = filter.Scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (filter.Outpoints is { Count: > 0 })
        {
            var outpointPairs = filter.Outpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();
            query = query.Where(v => outpointPairs.Contains(v.TransactionId + v.TransactionOutputIndex));
        }

        // Filter by wallet IDs (join with WalletContracts)
        if (filter.WalletIds is { Length: > 0 })
        {
            var walletScripts = db.WalletContracts
                .Where(c => filter.WalletIds.Contains(c.WalletId))
                .Select(c => c.Script);
            query = query.Where(v => walletScripts.Contains(v.Script));
        }

        // Filter by spent state
        if (!filter.IncludeSpent)
        {
            query = query.Where(v =>
                (v.SpentByTransactionId ?? "").Length == 0 &&
                (v.SettledByTransactionId ?? "").Length == 0);
        }

        // Filter by recoverable state
        if (!filter.IncludeRecoverable)
        {
            query = query.Where(v => !v.Recoverable);
        }

        // Search text filter
        if (!string.IsNullOrEmpty(filter.SearchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(filter.SearchText) ||
                v.Script.Contains(filter.SearchText));
        }

        // Order by creation date (newest first) for consistent pagination
        query = query.OrderByDescending(v => v.SeenAt);

        // Pagination
        if (filter.Skip.HasValue)
        {
            query = query.Skip(filter.Skip.Value);
        }

        if (filter.Take.HasValue)
        {
            query = query.Take(filter.Take.Value);
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

    #region Plugin-specific methods (wallet-guarded)

    /// <summary>
    /// Gets a VTXO by outpoint for a specific wallet.
    /// </summary>
    public async Task<VTXO?> GetVtxoByOutpointAsync(
        string walletId,
        string transactionId,
        int outputIndex,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Get wallet's contract scripts to verify ownership
        var walletScripts = await db.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        return await db.Vtxos
            .Where(v => v.TransactionId == transactionId && v.TransactionOutputIndex == outputIndex)
            .Where(v => walletScripts.Contains(v.Script))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Gets unspent VTXOs for a wallet's contracts.
    /// </summary>
    public async Task<IReadOnlyList<VTXO>> GetUnspentVtxosByContractScriptsAsync(
        IEnumerable<string> contractScripts,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = contractScripts.ToHashSet();
        return await db.Vtxos
            .Where(v => scriptSet.Contains(v.Script))
            .Where(v => v.SpentByTransactionId == null || v.SpentByTransactionId == "")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Sums the balance of unspent, non-recoverable VTXOs for a wallet's contracts.
    /// </summary>
    public async Task<long> SumUnspentBalanceByContractScriptsAsync(
        IEnumerable<string> contractScripts,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = contractScripts.ToHashSet();
        return await db.Vtxos
            .Where(v => scriptSet.Contains(v.Script))
            .Where(v => (v.SpentByTransactionId == null || v.SpentByTransactionId == "") && !v.Recoverable)
            .SumAsync(v => v.Amount, cancellationToken);
    }

    /// <summary>
    /// Gets VTXOs with pagination and filtering by contract scripts.
    /// </summary>
    public async Task<IReadOnlyList<VTXO>> GetVtxosWithPaginationAsync(
        IEnumerable<string> contractScripts,
        int skip = 0,
        int count = 10,
        string? searchText = null,
        bool includeSpent = false,
        bool includeRecoverable = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = contractScripts.ToHashSet();
        var query = db.Vtxos.Where(v => scriptSet.Contains(v.Script));

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(searchText) ||
                v.Script.Contains(searchText));
        }

        if (!includeSpent)
        {
            query = query.Where(v =>
                (v.SpentByTransactionId ?? "").Length == 0 &&
                (v.SettledByTransactionId ?? "").Length == 0);
        }

        if (!includeRecoverable)
        {
            query = query.Where(v => !v.Recoverable);
        }

        return await query
            .OrderByDescending(v => v.SeenAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets VTXOs by contract scripts with optional outpoint filtering.
    /// </summary>
    public async Task<IReadOnlyList<VTXO>> GetVtxosByScriptsAndOutpointsAsync(
        IEnumerable<string> contractScripts,
        HashSet<OutPoint>? vtxoOutpoints = null,
        bool includeSpent = false,
        bool includeRecoverable = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = contractScripts.ToHashSet();
        var query = db.Vtxos.Where(v => scriptSet.Contains(v.Script));

        if (!includeSpent)
        {
            query = query.Where(v =>
                v.SpentByTransactionId == null && v.SettledByTransactionId == null);
        }

        if (!includeRecoverable)
        {
            query = query.Where(v => !v.Recoverable);
        }

        // Filter by specific VTXO outpoints if provided
        if (vtxoOutpoints != null && vtxoOutpoints.Count > 0)
        {
            // Convert outpoints to a format we can query
            var outpointPairs = vtxoOutpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();

            query = query.Where(vtxo => outpointPairs.Contains(vtxo.TransactionId + vtxo.TransactionOutputIndex));
        }

        return await query
            .OrderByDescending(v => v.SeenAt)
            .ToListAsync(cancellationToken);
    }

    #endregion
}
