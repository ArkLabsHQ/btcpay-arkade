using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using ArkSwap = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkSwap;
using NNarkArkSwap = NArk.Swaps.Models.ArkSwap;

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
            existing.Status = swap.Status;
            existing.UpdatedAt = swap.UpdatedAt.ToUniversalTime();
        }
        else
        {
            var entity = new ArkSwap
            {
                SwapId = swap.SwapId,
                WalletId = walletId,
                SwapType = swap.SwapType,
                Invoice = swap.Invoice,
                ExpectedAmount = swap.ExpectedAmount,
                ContractScript = swap.ContractScript,
                Status = swap.Status,
                Hash = swap.Hash,
                CreatedAt = swap.CreatedAt.ToUniversalTime(),
                UpdatedAt = swap.UpdatedAt.ToUniversalTime()
            };
            db.Swaps.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        SwapsChanged?.Invoke(this, swap);
    }

    public async Task<IReadOnlyCollection<NNarkArkSwap>> GetSwaps(
        string[]? walletIds = null,
        string[]? swapIds = null,
        bool? active = null,
        ArkSwapType[]? swapTypes = null,
        ArkSwapStatus[]? status = null,
        string[]? contractScripts = null,
        string[]? hashes = null,
        string[]? invoices = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Swaps.AsQueryable();

        if (walletIds is { Length: > 0 })
        {
            query = query.Where(s => walletIds.Contains(s.WalletId));
        }

        if (swapIds is { Length: > 0 })
        {
            query = query.Where(s => swapIds.Contains(s.SwapId));
        }

        if (active == true)
        {
            query = query.Where(s => s.Status == ArkSwapStatus.Pending || s.Status == ArkSwapStatus.Unknown);
        }
        else if (active == false)
        {
            query = query.Where(s => s.Status != ArkSwapStatus.Pending && s.Status != ArkSwapStatus.Unknown);
        }

        if (swapTypes is { Length: > 0 })
        {
            query = query.Where(s => swapTypes.Contains(s.SwapType));
        }

        if (status is { Length: > 0 })
        {
            query = query.Where(s => status.Contains(s.Status));
        }

        if (contractScripts is { Length: > 0 })
        {
            query = query.Where(s => contractScripts.Contains(s.ContractScript));
        }

        if (hashes is { Length: > 0 })
        {
            query = query.Where(s => hashes.Contains(s.Hash));
        }

        if (invoices is { Length: > 0 })
        {
            query = query.Where(s => invoices.Contains(s.Invoice));
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(s =>
                s.SwapId.Contains(searchText) ||
                s.Invoice.Contains(searchText) ||
                s.Hash.Contains(searchText));
        }

        query = query.OrderByDescending(s => s.CreatedAt);

        if (skip.HasValue)
        {
            query = query.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            query = query.Take(take.Value);
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToNNarkSwap).ToList();
    }

    public async Task<bool> UpdateSwapStatus(
        string walletId,
        string swapId,
        ArkSwapStatus status,
        string? failReason = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var swap = await db.Swaps
            .FirstOrDefaultAsync(s => s.SwapId == swapId && s.WalletId == walletId, cancellationToken);

        if (swap == null)
            return false;

        swap.Status = status;
        swap.FailReason = failReason;
        swap.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static NNarkArkSwap MapToNNarkSwap(ArkSwap entity)
    {
        return new NNarkArkSwap(
            SwapId: entity.SwapId,
            WalletId: entity.WalletId,
            SwapType: entity.SwapType,
            Invoice: entity.Invoice,
            ExpectedAmount: entity.ExpectedAmount,
            ContractScript: entity.ContractScript,
            Address: entity.Address ?? "",
            Status: entity.Status,
            FailReason: entity.FailReason,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Hash: entity.Hash
        );
    }
}
