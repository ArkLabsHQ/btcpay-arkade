using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Legacy;

public sealed class LegacySwapRepository(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
{
    public async Task<IReadOnlyCollection<LegacySwap>> GetSwaps(
        string[]? walletIds = null,
        LegacySwapType[]? swapTypes = null,
        LegacySwapStatus[]? status = null,
        string[]? contractScripts = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Swaps.AsNoTracking();
        if (walletIds is { Length: > 0 })
            query = query.Where(s => walletIds.Contains(s.WalletId));
        if (swapTypes is { Length: > 0 })
            query = query.Where(s => swapTypes.Contains(s.SwapType));
        if (status is { Length: > 0 })
            query = query.Where(s => status.Contains(s.Status));
        if (contractScripts is { Length: > 0 })
            query = query.Where(s => contractScripts.Contains(s.ContractScript));
        if (!string.IsNullOrEmpty(searchText))
            query = query.Where(s => s.SwapId.Contains(searchText) ||
                                     s.Invoice.Contains(searchText) || s.Hash.Contains(searchText));
        query = query.OrderByDescending(s => s.CreatedAt);
        if (skip.HasValue)
            query = query.Skip(Math.Max(0, skip.Value));
        if (take.HasValue)
            query = query.Take(Math.Clamp(take.Value, 0, 500));
        return await query.ToListAsync(cancellationToken);
    }
}
