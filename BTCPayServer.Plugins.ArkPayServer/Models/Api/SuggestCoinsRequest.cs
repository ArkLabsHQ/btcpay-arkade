namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Request to suggest optimal coin selection for a destination.
/// </summary>
public class SuggestCoinsRequest
{
    /// <summary>
    /// Destination type detected from address/invoice.
    /// </summary>
    public DestinationType DestinationType { get; set; }

    /// <summary>
    /// Required amount in satoshis. Null means "send all".
    /// </summary>
    public long? AmountSats { get; set; }

    /// <summary>
    /// Outpoints to exclude from selection (already used elsewhere).
    /// </summary>
    public List<string>? ExcludeOutpoints { get; set; }
}
