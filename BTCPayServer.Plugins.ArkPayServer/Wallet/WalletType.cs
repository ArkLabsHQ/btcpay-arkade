namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// Defines the type of Ark wallet being used.
/// </summary>
public enum WalletType
{
    /// <summary>
    /// Legacy wallet using a single nsec (Nostr-style) private key.
    /// Provides backwards compatibility with existing wallets.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// HD wallet using BIP-39 mnemonic and BIP-32/BIP-86 derivation.
    /// Recommended for new wallets.
    /// </summary>
    HD = 1
}
