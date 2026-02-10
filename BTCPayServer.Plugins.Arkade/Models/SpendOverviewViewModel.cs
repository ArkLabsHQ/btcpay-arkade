namespace BTCPayServer.Plugins.Arkade.Models;

public class SpendOverviewViewModel
{
    public List<string> PrefilledDestination { get; set; } = [];
    public string? Destination { get; set; }
    public ArkBalancesViewModel Balances { get; set; } = new();
}