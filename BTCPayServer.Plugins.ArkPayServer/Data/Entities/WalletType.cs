namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public enum WalletType
{
    /// <summary>
    /// Legacy single-key wallet stored as Bech32-encoded nsec
    /// </summary>
    Nsec = 0,

    /// <summary>
    /// HD wallet with BIP-86 Taproot derivation from mnemonic seed
    /// </summary>
    Mnemonic = 1
}
