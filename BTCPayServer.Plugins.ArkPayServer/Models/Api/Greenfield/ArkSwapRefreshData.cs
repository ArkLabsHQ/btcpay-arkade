namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>The outcome of re-reading one swap from the chain and acting on it.</summary>
public class ArkSwapRefreshData
{
    public string SwapId { get; set; } = "";

    public string PreviousStatus { get; set; } = "";

    public string Status { get; set; } = "";

    /// <summary>Whether a swap its deadline had closed was put back under watch.</summary>
    public bool Reopened { get; set; }

    /// <summary>What the advance step reported when it could not act, e.g. a refund not yet due.</summary>
    public string? Detail { get; set; }
}
