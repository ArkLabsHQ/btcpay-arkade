namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Boltz swap limits and fees.
/// </summary>
public class ArkBoltzLimitsData
{
    public ArkSwapLimitData? Submarine { get; set; }
    public ArkSwapLimitData? Reverse { get; set; }
}

public class ArkSwapLimitData
{
    public long MinAmountSats { get; set; }
    public long MaxAmountSats { get; set; }
    public decimal FeePercentage { get; set; }
    public long MinerFeeSats { get; set; }
}
