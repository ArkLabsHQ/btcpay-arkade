using Microsoft.Extensions.Caching.Memory;
using NArk.Swaps.Abstractions;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Whether a wallet still has pre-migration Boltz swaps to show.
/// </summary>
/// <remarks>
/// <para>
/// The legacy swaps page exists for stores that migrated with swaps still on the books. For every
/// other store it is a second, permanently empty page sitting next to the real one, so it is hidden
/// rather than shown empty — and hiding it needs an answer cheap enough to ask while rendering the
/// store navigation, which happens on every page.
/// </para>
/// <para>
/// Cached hard, because the answer barely moves: nothing creates a Boltz swap any more, so it can
/// only ever go from true to false, and only when rows are deleted. A stale <c>true</c> keeps an
/// empty page reachable for a few more minutes; a stale <c>false</c> cannot hide a new swap, because
/// there are no new swaps.
/// </para>
/// </remarks>
public class ArkadeLegacySwapsService(ISwapStorage swapStorage, IMemoryCache memoryCache)
{
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(30);

    /// <summary>Whether this wallet has any Boltz-era swap rows.</summary>
    /// <param name="walletId">The wallet to check. A blank id has no swaps.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<bool> HasLegacySwapsAsync(
        string? walletId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(walletId))
        {
            return false;
        }

        var key = $"arkade-legacy-swaps-{walletId}";
        if (memoryCache.TryGetValue<bool>(key, out var cached))
        {
            return cached;
        }

        // take: 1 — this asks whether any row exists, not how many.
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId], take: 1, cancellationToken: cancellationToken);

        var hasAny = swaps.Count > 0;
        memoryCache.Set(key, hasAny, CacheExpiry);
        return hasAny;
    }
}
