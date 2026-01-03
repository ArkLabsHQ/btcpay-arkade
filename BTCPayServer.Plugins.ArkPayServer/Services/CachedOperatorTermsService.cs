using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Cached wrapper for IClientTransport.GetServerInfoAsync()
/// </summary>
public class CachedServerInfoService
{
    private readonly IClientTransport _clientTransport;
    private readonly ILogger<CachedServerInfoService> _logger;
    private readonly IMemoryCache _memoryCache;
    private const string CacheKey = "ArkServerInfo";

    public CachedServerInfoService(
        IClientTransport clientTransport,
        ILogger<CachedServerInfoService> logger,
        IMemoryCache memoryCache)
    {
        _clientTransport = clientTransport;
        _logger = logger;
        _memoryCache = memoryCache;
    }

    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var serverInfo = await _memoryCache.GetOrCreateAsync(CacheKey, async entry =>
        {
            _logger.LogDebug("Fetching server info from Ark operator");
            var info = await _clientTransport.GetServerInfoAsync(cancellationToken);
            entry.AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(1);
            return info;
        });

        if (serverInfo is null)
        {
            _memoryCache.Remove(CacheKey);
            throw new InvalidOperationException("Failed to fetch server info");
        }

        return serverInfo;
    }

    public void InvalidateCache()
    {
        _memoryCache.Remove(CacheKey);
    }
}
