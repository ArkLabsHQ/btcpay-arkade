using System.Collections.Concurrent;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Caches Arkade asset metadata (name, ticker, decimals, icon) resolved
/// from the arkd indexer. Asset metadata is immutable after issuance, so a
/// process-lifetime in-memory cache avoids repeated indexer round-trips on
/// every dashboard/checkout render.
/// <para>
/// The success cache is unbounded and never evicted: this is intentional —
/// asset metadata is immutable, and a BTCPay store realistically configures
/// a handful of accepted assets (not thousands), so the entry count is
/// naturally small. Failed lookups are negatively cached for a short TTL so
/// an indexer outage doesn't turn every render into N× network timeouts.
/// </para>
/// </summary>
public class AssetMetadataService(IClientTransport clientTransport)
{
    private readonly ConcurrentDictionary<string, ArkAssetDetails> _cache = new();

    /// <summary>Asset ids whose last lookup failed, with the time it failed.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeCache = new();

    /// <summary>How long a failed lookup is remembered before we retry the indexer.</summary>
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resolves details for <paramref name="assetId"/>, caching the result.
    /// Returns null if the indexer has no record (or is unreachable) — the
    /// caller falls back to showing the raw asset id. A null result is
    /// negatively cached for <see cref="NegativeCacheTtl"/> so a down
    /// indexer isn't hit again on every subsequent render.
    /// </summary>
    public async Task<ArkAssetDetails?> GetAssetDetailsAsync(
        string assetId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(assetId, out var cached))
            return cached;

        if (_negativeCache.TryGetValue(assetId, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < NegativeCacheTtl)
            return null;

        try
        {
            var details = await clientTransport.GetAssetDetailsAsync(assetId, cancellationToken);
            if (details is null)
            {
                _negativeCache[assetId] = DateTimeOffset.UtcNow;
                return null;
            }
            _cache.TryAdd(assetId, details);
            _negativeCache.TryRemove(assetId, out _);
            return details;
        }
        catch
        {
            // Indexer miss/unreachable: not fatal — UI degrades to raw id.
            // Remember the failure briefly so we don't re-hit a down indexer
            // on every dashboard/checkout/prompt render.
            _negativeCache[assetId] = DateTimeOffset.UtcNow;
            return null;
        }
    }

    public string? GetName(ArkAssetDetails? details) =>
        details?.Metadata is { } m && m.TryGetValue("name", out var v) ? v : null;

    public string? GetTicker(ArkAssetDetails? details) =>
        details?.Metadata is { } m && m.TryGetValue("ticker", out var v) ? v : null;

    /// <summary>Decimal precision for display. Defaults to 0 when unset.</summary>
    public int GetDecimals(ArkAssetDetails? details) =>
        details?.Metadata is { } m && m.TryGetValue("decimals", out var s) &&
        int.TryParse(s, out var d) && d is >= 0 and <= 18
            ? d
            : 0;

    /// <summary>
    /// Formats a base-unit amount for display using the asset's declared
    /// decimals (e.g. 150 with decimals=2 → "1.5"). Delegates to the
    /// canonical <see cref="AssetAmount.Format"/> so display and the rate
    /// resolver never diverge on the divisor.
    /// </summary>
    public string FormatAmount(ulong amount, ArkAssetDetails? details) =>
        AssetAmount.Format(amount, GetDecimals(details));
}
