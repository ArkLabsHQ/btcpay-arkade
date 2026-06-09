namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkBalancesViewModel
{
    public decimal AvailableBalance { get; set; }
    public decimal LockedBalance { get; set; }
    public decimal RecoverableBalance { get; set; }
    public decimal UnspendableBalance { get; set; }
    public decimal BoardingBalance { get; set; }

    /// <summary>
    /// Arkade asset holdings carried on spendable VTXOs, aggregated per
    /// asset id and enriched with indexer metadata. Empty when the wallet
    /// holds no assets.
    /// </summary>
    public IReadOnlyList<AssetBalanceViewModel> AssetBalances { get; set; } = [];
}

/// <summary>
/// One Arkade asset's spendable balance with display metadata.
/// </summary>
public class AssetBalanceViewModel
{
    public required string AssetId { get; init; }
    public string? Name { get; init; }
    public string? Ticker { get; init; }
    public int Decimals { get; init; }

    /// <summary>Raw base-unit amount.</summary>
    public ulong Amount { get; init; }

    /// <summary>Amount formatted with the asset's declared decimals.</summary>
    public required string FormattedAmount { get; init; }
}
