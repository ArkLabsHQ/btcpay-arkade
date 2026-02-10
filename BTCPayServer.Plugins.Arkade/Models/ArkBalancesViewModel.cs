namespace BTCPayServer.Plugins.Arkade.Models;

public class ArkBalancesViewModel
{
    public decimal AvailableBalance { get; set; }
    public decimal LockedBalance { get; set; }
    public decimal RecoverableBalance { get; set; }
    public decimal UnspendableBalance { get; set; }
}
