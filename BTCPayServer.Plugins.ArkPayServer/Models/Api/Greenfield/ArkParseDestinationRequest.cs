namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to parse and classify a destination string without spending anything.
/// </summary>
public class ArkParseDestinationRequest
{
    /// <summary>
    /// Destination input: bare Ark address, Bitcoin address, BOLT11 invoice (with or without
    /// <c>lightning:</c> prefix), LNURL, Lightning Address (<c>user@host</c>), or a BIP21 URI.
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Optional caller-supplied amount in BTC. When supplied it is converted to sats and
    /// preferred over any amount embedded in the destination.
    /// </summary>
    public decimal? AmountBtc { get; set; }
}
