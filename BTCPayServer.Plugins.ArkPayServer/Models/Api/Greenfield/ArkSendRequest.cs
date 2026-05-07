namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to send Ark funds to a destination.
/// </summary>
public class ArkSendRequest
{
    /// <summary>
    /// Destination: Ark address, Bitcoin address, BIP21 URI, or Lightning invoice/LNURL.
    /// </summary>
    public string Destination { get; set; } = "";

    /// <summary>
    /// Amount in satoshis. Null means "send all available".
    /// </summary>
    public long? AmountSats { get; set; }

    /// <summary>
    /// Specific VTXO outpoints to use as inputs. If empty, coins are selected automatically.
    /// </summary>
    public List<string>? InputOutpoints { get; set; }
}

/// <summary>
/// Response after submitting a send request.
/// </summary>
public class ArkSendResponse
{
    /// <summary>
    /// Transaction ID (Ark TX hash or Lightning payment hash).
    /// </summary>
    public string? TxId { get; set; }

    /// <summary>
    /// Swap ID if this triggered a Lightning or chain swap.
    /// </summary>
    public string? SwapId { get; set; }
}
