using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// IPluginWallet implementation for legacy single-key (nsec) wallets.
/// Provides backwards compatibility with existing wallets that use Nostr-style keys.
/// </summary>
public class LegacyNsecWallet : IPluginWallet
{
    private readonly SingleKeySigningEntity _signingEntity;

    public string WalletId { get; }
    public WalletType Type => WalletType.Legacy;

    public LegacyNsecWallet(SingleKeySigningEntity signingEntity)
    {
        _signingEntity = signingEntity;
        WalletId = _signingEntity.GetFingerprint().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates a legacy wallet from an nsec string.
    /// </summary>
    public static LegacyNsecWallet FromNsec(string nsec, Network network)
    {
        var signingEntity = SingleKeySigningEntity.FromNsec(nsec, network);
        return new LegacyNsecWallet(signingEntity);
    }

    /// <summary>
    /// Creates a legacy wallet from a hex private key.
    /// </summary>
    public static LegacyNsecWallet FromHex(string hexPrivateKey, Network network)
    {
        var signingEntity = SingleKeySigningEntity.FromHex(hexPrivateKey, network);
        return new LegacyNsecWallet(signingEntity);
    }

    /// <summary>
    /// For legacy wallets, always returns the same signing entity.
    /// Contracts are differentiated by tweak values, not different keys.
    /// </summary>
    public Task<ISigningEntity> GetNewSigningEntity(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ISigningEntity>(_signingEntity);
    }

    /// <summary>
    /// For legacy wallets, we can only sign if the descriptor matches our single key.
    /// </summary>
    public Task<ISigningEntity?> FindSigningEntity(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        // For legacy wallets, we need to check if the descriptor's pubkey matches ours
        // This is a simplified check - in practice we'd need to extract and compare pubkeys
        var ourDescriptor = _signingEntity.GetOutputDescriptor(cancellationToken).GetAwaiter().GetResult();

        // If the descriptor starts with the same key, we can sign for it
        // The full descriptor might have tweaks applied, but the base key should match
        if (descriptor.ToString().Contains(ourDescriptor.ToString().Replace("tr(", "").TrimEnd(')')))
        {
            return Task.FromResult<ISigningEntity?>(_signingEntity);
        }

        return Task.FromResult<ISigningEntity?>(null);
    }

    /// <summary>
    /// For legacy wallets, the default receive descriptor is just the single key.
    /// </summary>
    public Task<OutputDescriptor> GetDefaultReceiveDescriptor(CancellationToken cancellationToken = default)
    {
        return _signingEntity.GetOutputDescriptor(cancellationToken);
    }
}
