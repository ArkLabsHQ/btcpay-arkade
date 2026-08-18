using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Owns the per-wallet Lightning spend capability: issuing it, regenerating it, and verifying
/// it on spend paths.
///
/// A wallet has exactly one capability, created on first use and stable thereafter, so the
/// same connection string can be shared across the stores its owner controls. It is cached in
/// memory — spend paths verify without a storage round trip — and the cache is written through
/// on issue and regenerate.
/// </summary>
public class ArkLightningSpendKeyService(IWalletStorage walletStorage)
{
    private readonly ConcurrentDictionary<string, string> _cache = new();

    /// <summary>
    /// Returns the wallet's capability, creating one on first use.
    /// </summary>
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

    /// <summary>
    /// Issues a fresh capability for the wallet, superseding the previous one. Connection
    /// strings already shared with other stores stop authorising spends.
    /// </summary>
    public Task<string> RegenerateAsync(string walletId, CancellationToken cancellationToken = default)
        => IssueAsync(walletId, cancellationToken);

    /// <summary>
    /// Verifies a presented capability against the wallet's. Fails closed: a missing capability
    /// on either side is never treated as permissive.
    /// </summary>
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

    /// <summary>
    /// Builds the connection string carrying the wallet's capability. This is the value shown
    /// to an owner for adding the wallet to another store they control.
    /// </summary>
    public async Task<string> BuildConnectionStringAsync(string walletId,
        CancellationToken cancellationToken = default)
        => $"{BuildReceiveOnlyConnectionString(walletId)};spend-key={await GetOrCreateAsync(walletId, cancellationToken)}";

    /// <summary>
    /// Builds a connection string without a capability. Such a client can watch and receive
    /// but not spend.
    /// </summary>
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
        // Hex rather than base64: the value has to survive `key=value;` parsing, and base64's
        // '=' padding would truncate it.
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await walletStorage.SetMetadataValue(
            walletId, ArkLightningClient.SpendKeyMetadataKey, key, cancellationToken);
        _cache[walletId] = key;
        return key;
    }
}
