namespace BTCPayServer.Plugins.Arkade.Models;

/// <summary>
/// View model for the Arkade dashboard widget.
/// </summary>
public class ArkDashboardWidgetViewModel
{
    public required string StoreId { get; set; }
    public string? WalletId { get; set; }
    public bool HasWallet { get; set; }
    public ArkBalancesViewModel? Balances { get; set; }
    public int ActiveContractsCount { get; set; }
}
