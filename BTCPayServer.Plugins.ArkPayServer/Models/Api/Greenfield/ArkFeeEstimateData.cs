namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Result of a fee estimation for a prospective Arkade send.
/// </summary>
public class ArkFeeEstimateData
{
    /// <summary>
    /// Estimated total fee in satoshis. Zero for fee-free Arkade-only sends (no on-chain outputs).
    /// For Lightning destinations the fee is the solver's spread, quoted per swap.
    /// </summary>
    public long EstimatedFeeSats { get; set; }

    /// <summary>
    /// Human-readable description of where the fee comes from
    /// (e.g. "No fee for Arkade transactions", "Batch transaction fee").
    /// </summary>
    public string? FeeDescription { get; set; }

    /// <summary>
    /// True when the destination is a Lightning invoice / LNURL, in which case the fee breakdown
    /// reflects the solver's swap fee rather than Arkade batch fees.
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// Solver fee applied to the amount, as a percentage (e.g. 0.5 for 0.5%).
    /// Only meaningful when <see cref="IsLightning"/> is true.
    /// </summary>
    public decimal FeePercentage { get; set; }

    /// <summary>
    /// Miner fee in satoshis. Only meaningful when <see cref="IsLightning"/> is true.
    /// </summary>
    public long MinerFeeSats { get; set; }

    /// <summary>
    /// Sum of input satoshis used by the estimate. When the server auto-selects coins,
    /// this reflects the auto-selection result.
    /// </summary>
    public long TotalInputSats { get; set; }

    /// <summary>
    /// Number of coins included in the estimate (auto-selected or explicitly supplied).
    /// </summary>
    public int SelectedCoinCount { get; set; }

    /// <summary>
    /// Outpoints selected by the server when <c>CoinSelectionMode = "auto"</c>. Always populated for
    /// auto mode so the client can mirror the server-side selection in its UI.
    /// </summary>
    public List<string>? SelectedOutpoints { get; set; }
}
