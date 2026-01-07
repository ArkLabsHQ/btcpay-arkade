using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using PluginArkSwap = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkSwap;
using PluginArkSwapStatus = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkSwapStatus;
using PluginArkSwapType = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkSwapType;
using NNarkArkSwap = NArk.Swaps.Models.ArkSwap;
using NNarkArkSwapStatus = NArk.Swaps.Models.ArkSwapStatus;
using NNarkArkSwapType = NArk.Swaps.Models.ArkSwapType;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's ISwapStorage interface.
/// Maps between plugin's ArkSwap entity and NNark's ArkSwap record.
/// </summary>
public class EfCoreSwapStorage : ISwapStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    public event EventHandler<NNarkArkSwap>? SwapsChanged;

    public EfCoreSwapStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task SaveSwap(string walletId, NNarkArkSwap swap, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Swaps.FirstOrDefaultAsync(
            s => s.SwapId == swap.SwapId && s.WalletId == walletId,
            cancellationToken);

        if (existing != null)
        {
            // Update existing
            existing.Status = MapStatus(swap.Status);
            existing.UpdatedAt = swap.UpdatedAt;
        }
        else
        {
            var entity = new PluginArkSwap
            {
                SwapId = swap.SwapId,
                WalletId = walletId,
                SwapType = MapType(swap.SwapType),
                Invoice = swap.Invoice,
                ExpectedAmount = swap.ExpectedAmount,
                ContractScript = swap.ContractScript,
                Status = MapStatus(swap.Status),
                Hash = swap.Hash,
                CreatedAt = swap.CreatedAt,
                UpdatedAt = swap.UpdatedAt
            };
            db.Swaps.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        SwapsChanged?.Invoke(this, swap);
    }

    public async Task<NNarkArkSwap> GetSwap(string swapId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.Swaps.FirstOrDefaultAsync(
            s => s.SwapId == swapId,
            cancellationToken);

        if (entity == null)
            throw new InvalidOperationException($"Swap not found: {swapId}");

        return MapToNNarkSwap(entity);
    }

    public async Task<IReadOnlyCollection<NNarkArkSwap>> GetActiveSwaps(
        string? walletId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Swaps.Where(s => s.Status == PluginArkSwapStatus.Pending);

        if (walletId != null)
        {
            query = query.Where(s => s.WalletId == walletId);
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToNNarkSwap).ToList();
    }

    private static NNarkArkSwap MapToNNarkSwap(PluginArkSwap entity)
    {
        return new NNarkArkSwap(
            SwapId: entity.SwapId,
            WalletId: entity.WalletId,
            SwapType: MapType(entity.SwapType),
            Invoice: entity.Invoice,
            ExpectedAmount: entity.ExpectedAmount,
            ContractScript: entity.ContractScript,
            Address: entity.Address ?? "",
            Status: MapStatus(entity.Status),
            FailReason: entity.FailReason,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Hash: entity.Hash
        );
    }

    private static NNarkArkSwapStatus MapStatus(PluginArkSwapStatus status) => status switch
    {
        PluginArkSwapStatus.Pending => NNarkArkSwapStatus.Pending,
        PluginArkSwapStatus.Settled => NNarkArkSwapStatus.Settled,
        PluginArkSwapStatus.Failed => NNarkArkSwapStatus.Failed,
        PluginArkSwapStatus.Refunded => NNarkArkSwapStatus.Refunded,
        PluginArkSwapStatus.Unknown => NNarkArkSwapStatus.Unknown,
        _ => NNarkArkSwapStatus.Unknown
    };

    private static PluginArkSwapStatus MapStatus(NNarkArkSwapStatus status) => status switch
    {
        NNarkArkSwapStatus.Pending => PluginArkSwapStatus.Pending,
        NNarkArkSwapStatus.Settled => PluginArkSwapStatus.Settled,
        NNarkArkSwapStatus.Failed => PluginArkSwapStatus.Failed,
        NNarkArkSwapStatus.Refunded => PluginArkSwapStatus.Refunded,
        NNarkArkSwapStatus.Unknown => PluginArkSwapStatus.Unknown,
        _ => PluginArkSwapStatus.Unknown
    };

    private static NNarkArkSwapType MapType(PluginArkSwapType type) => type switch
    {
        PluginArkSwapType.ReverseSubmarine => NNarkArkSwapType.ReverseSubmarine,
        PluginArkSwapType.Submarine => NNarkArkSwapType.Submarine,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static PluginArkSwapType MapType(NNarkArkSwapType type) => type switch
    {
        NNarkArkSwapType.ReverseSubmarine => PluginArkSwapType.ReverseSubmarine,
        NNarkArkSwapType.Submarine => PluginArkSwapType.Submarine,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    #region Plugin-specific methods (wallet-guarded)

    /// <summary>
    /// Gets swaps grouped by contract script. Wallet ID guards ownership.
    /// </summary>
    public async Task<Dictionary<string, PluginArkSwap[]>> GetSwapsByContractScriptsAsync(
        string walletId,
        IEnumerable<string> contractScripts,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = contractScripts.ToHashSet();
        var swaps = await db.Swaps
            .Where(s => s.WalletId == walletId && scriptSet.Contains(s.ContractScript))
            .OrderByDescending(s => s.CreatedAt)
            .ToArrayAsync(cancellationToken);

        return swaps
            .GroupBy(s => s.ContractScript)
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>
    /// Gets a swap by ID with wallet ownership guard.
    /// </summary>
    public async Task<PluginArkSwap?> GetSwapByIdAsync(
        string walletId,
        string swapId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Swaps
            .FirstOrDefaultAsync(s => s.SwapId == swapId && s.WalletId == walletId, cancellationToken);
    }

    /// <summary>
    /// Updates swap status. Wallet ID guards ownership.
    /// </summary>
    public async Task<bool> UpdateSwapStatusAsync(
        string walletId,
        string swapId,
        PluginArkSwapStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var swap = await db.Swaps
            .FirstOrDefaultAsync(s => s.SwapId == swapId && s.WalletId == walletId, cancellationToken);

        if (swap == null)
            return false;

        swap.Status = status;
        swap.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Gets a swap by ID with its contract included. Wallet ID guards ownership.
    /// </summary>
    public async Task<PluginArkSwap?> GetSwapWithContractAsync(
        string walletId,
        string swapId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Swaps
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(s => s.SwapId == swapId && s.WalletId == walletId, cancellationToken);
    }

    /// <summary>
    /// Gets a swap by payment hash with its contract included. Wallet ID guards ownership.
    /// </summary>
    public async Task<PluginArkSwap?> GetSwapByHashWithContractAsync(
        string walletId,
        string paymentHash,
        PluginArkSwapType? swapType = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Swaps
            .Include(s => s.Contract)
            .Where(s => s.Hash == paymentHash && s.WalletId == walletId);

        if (swapType.HasValue)
        {
            query = query.Where(s => s.SwapType == swapType.Value);
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a swap by invoice with its contract included. Wallet ID guards ownership.
    /// </summary>
    public async Task<PluginArkSwap?> GetSwapByInvoiceWithContractAsync(
        string walletId,
        string invoice,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Swaps
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(s => s.Invoice == invoice && s.WalletId == walletId, cancellationToken);
    }

    /// <summary>
    /// Lists reverse submarine swaps (invoices) with their contracts. Wallet ID guards ownership.
    /// </summary>
    public async Task<IReadOnlyList<PluginArkSwap>> ListReverseSwapsWithContractAsync(
        string walletId,
        bool? pendingOnly = null,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Swaps
            .Include(s => s.Contract)
            .Where(s => s.SwapType == PluginArkSwapType.ReverseSubmarine && s.WalletId == walletId);

        if (pendingOnly == true)
        {
            query = query.Where(s => s.Status == PluginArkSwapStatus.Pending);
        }

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lists submarine swaps (payments) with their contracts. Wallet ID guards ownership.
    /// </summary>
    public async Task<IReadOnlyList<PluginArkSwap>> ListSubmarineSwapsWithContractAsync(
        string walletId,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Swaps
            .Include(s => s.Contract)
            .Where(s => s.SwapType == PluginArkSwapType.Submarine && s.WalletId == walletId)
            .Skip(skip)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets swaps with pagination and optional filtering.
    /// </summary>
    public async Task<IReadOnlyList<PluginArkSwap>> GetSwapsWithPaginationAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string? searchText = null,
        PluginArkSwapStatus? status = null,
        PluginArkSwapType? swapType = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Swaps
            .Include(s => s.Contract)
            .Where(s => s.WalletId == walletId);

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(s =>
                s.SwapId.Contains(searchText) ||
                s.Invoice.Contains(searchText) ||
                s.Hash.Contains(searchText));
        }

        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        if (swapType.HasValue)
        {
            query = query.Where(s => s.SwapType == swapType.Value);
        }

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    #endregion
}
