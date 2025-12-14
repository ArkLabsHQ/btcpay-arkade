using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreOverviewViewModel
{
    public bool IsLightningEnabled { get; set; }
    public bool IsDestinationSweepEnabled { get; set; }
    public ArkBalancesViewModel? Balances { get; set; }
    public string? WalletId { get; set; }
    public string? Destination { get; set; }
    public bool SignerAvailable { get; set; }
    public string? Wallet { get; set; }
    public string? DefaultAddress { get; set; }
    public bool AllowSubDustAmounts { get; set; }
    public WalletType WalletType { get; set; }
    
    // Service connection status
    public string? ArkOperatorUrl { get; set; }
    public bool ArkOperatorConnected { get; set; }
    public string? ArkOperatorError { get; set; }
    
    public string? BoltzUrl { get; set; }
    public bool BoltzConnected { get; set; }
    public string? BoltzError { get; set; }
    
    // Boltz limits for Lightning - Reverse Swap (Receiving Lightning)
    public long? BoltzReverseMinAmount { get; set; }
    public long? BoltzReverseMaxAmount { get; set; }
    public decimal? BoltzReverseFeePercentage { get; set; }
    public long? BoltzReverseMinerFee { get; set; }
    
    // Boltz limits for Lightning - Submarine Swap (Sending Lightning)
    public long? BoltzSubmarineMinAmount { get; set; }
    public long? BoltzSubmarineMaxAmount { get; set; }
    public decimal? BoltzSubmarineFeePercentage { get; set; }
    public long? BoltzSubmarineMinerFee { get; set; }
}