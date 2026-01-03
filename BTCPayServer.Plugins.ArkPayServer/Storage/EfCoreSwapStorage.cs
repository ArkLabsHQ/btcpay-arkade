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
}
