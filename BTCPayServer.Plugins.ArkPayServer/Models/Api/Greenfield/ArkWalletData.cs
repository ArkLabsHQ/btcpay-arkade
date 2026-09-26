namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Wallet information exposed via the Greenfield API.
/// </summary>
public class ArkWalletData
{
    public string WalletId { get; set; } = "";
    public string WalletType { get; set; } = "";
    public bool SignerAvailable { get; set; }
    public bool IsOwnedByStore { get; set; }
    public string? DefaultAddress { get; set; }
    public string? Destination { get; set; }
    public bool AllowSubDustAmounts { get; set; }
    public bool BoardingEnabled { get; set; }

    /// <summary>Whether onchain payments are settled through a solver rather than by boarding.</summary>
    public bool OnchainSwapEnabled { get; set; }
    public long MinBoardingAmountSats { get; set; }
    public bool LightningEnabled { get; set; }
}
