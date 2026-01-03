using NArk.Abstractions.Wallets;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// Bridge interface between BTCPay plugin wallet management and NNark's ISigningEntity.
/// Supports both legacy (single-key nsec) and HD (mnemonic-based) wallet types.
/// </summary>
public interface IPluginWallet
{
    /// <summary>
    /// Unique identifier for this wallet.
    /// For legacy wallets, this is the public key hex.
    /// For HD wallets, this is the master fingerprint.
    /// </summary>
    string WalletId { get; }

    /// <summary>
    /// The type of wallet (Legacy or HD).
    /// </summary>
    WalletType Type { get; }

    /// <summary>
    /// Gets a new signing entity for creating a payment contract.
    /// For legacy wallets, always returns the same single key.
    /// For HD wallets, increments the derivation index and returns a new key.
    /// </summary>
    Task<ISigningEntity> GetNewSigningEntity(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the signing entity that corresponds to the given output descriptor.
    /// Used when we need to sign for an existing contract.
    /// </summary>
    Task<ISigningEntity?> FindSigningEntity(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the default receive address for this wallet.
    /// Used as the destination for swept funds.
    /// </summary>
    Task<OutputDescriptor> GetDefaultReceiveDescriptor(CancellationToken cancellationToken = default);
}
