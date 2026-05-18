using System.Collections.Concurrent;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Caches Arkade asset metadata (name, ticker, decimals, icon) resolved
/// from the arkd indexer. Asset metadata is immutable after issuance, so a
/// process-lifetime in-memory cache avoids repeated indexer round-trips on
/// every dashboard/checkout render.
/// </summary>
public class AssetMetadataService(IClientTransport clientTransport)
{
    private readonly ConcurrentDictionary<string, ArkAssetDetails> _cache = new();

    /// <summary>
    /// Resolves details for <paramref name="assetId"/>, caching the result.
    /// Returns null if the indexer has no record (or is unreachable) — the
    /// caller falls back to showing the raw asset id.
    /// </summary>
    public async Task<ArkAssetDetails?> GetAssetDetailsAsync(
        string assetId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(assetId, out var cached))
            return cached;

        try
        {
            var details = await clientTransport.GetAssetDetailsAsync(assetId, cancellationToken);
            _cache.TryAdd(assetId, details);
            return details;
        }
        catch
        {
            // Indexer miss/unreachable: not fatal — UI degrades to raw id.
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
    /// decimals (e.g. 150 with decimals=2 → "1.50").
    /// </summary>
    public string FormatAmount(ulong amount, ArkAssetDetails? details)
    {
        var decimals = GetDecimals(details);
        if (decimals == 0) return amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var divisor = (decimal)Math.Pow(10, decimals);
        return (amount / divisor).ToString(
            "0." + new string('#', decimals), System.Globalization.CultureInfo.InvariantCulture);
    }
}
