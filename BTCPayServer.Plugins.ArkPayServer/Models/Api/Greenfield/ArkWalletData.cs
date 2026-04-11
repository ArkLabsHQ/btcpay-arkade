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
    public long MinBoardingAmountSats { get; set; }
    public bool LightningEnabled { get; set; }
}
