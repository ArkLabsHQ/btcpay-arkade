using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// One stable spend capability per wallet, so its connection string can be shared across the owner's stores.
/// </summary>
public class ArkLightningSpendKeyService(IWalletStorage walletStorage)
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public async Task<string> GetOrCreateAsync(string walletId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(walletId, out var cached)) return cached;

        var stored = await ReadStoredAsync(walletId, cancellationToken);
        if (!string.IsNullOrEmpty(stored))
        {
            _cache[walletId] = stored;
            return stored;
        }

        return await IssueAsync(walletId, cancellationToken);
    }

    // Supersedes the old key: connection strings already shared stop authorising spends.
    public Task<string> RegenerateAsync(string walletId, CancellationToken cancellationToken = default)
        => IssueAsync(walletId, cancellationToken);

    public async Task<bool> VerifyAsync(string walletId, string? presented,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        if (!_cache.TryGetValue(walletId, out var expected))
        {
            expected = await ReadStoredAsync(walletId, cancellationToken);
            if (!string.IsNullOrEmpty(expected)) _cache[walletId] = expected;
        }

        if (string.IsNullOrEmpty(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    public async Task<string> BuildConnectionStringAsync(string walletId,
        CancellationToken cancellationToken = default)
        => $"{BuildReceiveOnlyConnectionString(walletId)};spend-key={await GetOrCreateAsync(walletId, cancellationToken)}";

    public static string BuildReceiveOnlyConnectionString(string walletId)
        => $"type=arkade;wallet-id={walletId}";

    private async Task<string?> ReadStoredAsync(string walletId, CancellationToken cancellationToken)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        return wallet?.Metadata?.TryGetValue(ArkLightningClient.SpendKeyMetadataKey, out var stored) is true
            ? stored
            : null;
    }

    private async Task<string> IssueAsync(string walletId, CancellationToken cancellationToken)
    {
        // Hex, not base64: '=' padding would break `key=value;` parsing.
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await walletStorage.SetMetadataValue(
            walletId, ArkLightningClient.SpendKeyMetadataKey, key, cancellationToken);
        _cache[walletId] = key;
        return key;
    }
}
