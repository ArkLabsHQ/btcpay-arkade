using System.Collections.Concurrent;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class AssetMetadataService(IClientTransport clientTransport)
{
    private readonly ConcurrentDictionary<string, ArkAssetDetails> _cache = new();

    public async Task<ArkAssetDetails?> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
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
            return null;
        }
    }

    public string? GetName(ArkAssetDetails? details)
    {
        if (details?.Metadata is null) return null;
        details.Metadata.TryGetValue("name", out var name);
        return name;
    }

    public string? GetTicker(ArkAssetDetails? details)
    {
        if (details?.Metadata is null) return null;
        details.Metadata.TryGetValue("ticker", out var ticker);
        return ticker;
    }

    public int GetDecimals(ArkAssetDetails? details)
    {
        if (details?.Metadata is null) return 0;
        if (details.Metadata.TryGetValue("decimals", out var decimalsStr) && int.TryParse(decimalsStr, out var decimals))
            return decimals;
        return 0;
    }
}
