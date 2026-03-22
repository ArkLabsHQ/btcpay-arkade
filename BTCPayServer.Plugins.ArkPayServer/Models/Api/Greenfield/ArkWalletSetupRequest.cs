namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to create or import an Arkade wallet for a store.
/// </summary>
public class ArkWalletSetupRequest
{
    /// <summary>
    /// Wallet secret or identifier. Supports:
    /// - null/empty: generates a new 12-word BIP-39 mnemonic
    /// - nsec private key: imports as single-key wallet
    /// - 12 or 24 word BIP-39 mnemonic: imports as HD wallet
    /// - Ark address: generates a new wallet and sets it as the destination
    /// - Existing wallet ID: links the wallet to this store (no new wallet created)
    /// </summary>
    public string? Wallet { get; set; }

    /// <summary>
    /// Optional Ark address to set as the wallet's default destination (auto-sweep target).
    /// Ignored if Wallet is an Ark address (which sets destination automatically).
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// If true, also configures Arkade Lightning (LN) for this store. Default: true.
    /// </summary>
    public bool EnableLightning { get; set; } = true;
}

/// <summary>
/// Response after wallet setup.
/// </summary>
public class ArkWalletSetupResponse
{
    public string WalletId { get; set; } = "";
    public string WalletType { get; set; } = "";
    public bool IsNewWallet { get; set; }
    public bool LightningEnabled { get; set; }

    /// <summary>
    /// The generated mnemonic phrase (only returned for newly generated wallets, not for imports).
    /// </summary>
    public string? Mnemonic { get; set; }
}
