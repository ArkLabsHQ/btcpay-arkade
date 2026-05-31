namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to estimate fees for a prospective Arkade send.
/// </summary>
public class ArkFeeEstimateRequest
{
    /// <summary>
    /// Output destinations and amounts to estimate against. At least one destination is recommended;
    /// an empty list is treated as a consolidation (send-all-to-self) estimate.
    /// </summary>
    public List<ArkFeeEstimateOutput> Outputs { get; set; } = [];

    /// <summary>
    /// Explicit VTXO outpoints (<c>txid:vout</c>) to use as inputs. When empty and
    /// <see cref="CoinSelectionMode"/> is "auto", the server selects coins automatically.
    /// </summary>
    public List<string> InputOutpoints { get; set; } = [];

    /// <summary>
    /// Coin selection mode: <c>"auto"</c> for server-side selection, or <c>"manual"</c> (the default)
    /// to use only the coins listed in <see cref="InputOutpoints"/>.
    /// </summary>
    public string? CoinSelectionMode { get; set; }

    /// <summary>
    /// Preferred spend type: <c>"Arkade"</c> (offchain, no fee) or <c>"Batch"</c> (joins the next round).
    /// Ignored for Lightning destinations.
    /// </summary>
    public string? SpendType { get; set; }
}

/// <summary>
/// Single destination entry inside a fee estimate request.
/// </summary>
public class ArkFeeEstimateOutput
{
    /// <summary>
    /// Destination string: Ark address, Bitcoin address, BIP21 URI, or Lightning invoice / LNURL.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Amount in satoshis (optional). When omitted on a single-output request, the full input sum
    /// is treated as the amount.
    /// </summary>
    public long? AmountSats { get; set; }
}
