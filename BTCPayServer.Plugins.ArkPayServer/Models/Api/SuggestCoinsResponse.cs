namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Response with suggested coin selection.
/// </summary>
public class SuggestCoinsResponse
{
    /// <summary>
    /// Suggested outpoints to use (txid:vout format).
    /// </summary>
    public List<string> SuggestedOutpoints { get; set; } = new();

    /// <summary>
    /// Total amount of suggested coins in satoshis.
    /// </summary>
    public long TotalSats { get; set; }

    /// <summary>
    /// Detected spend type based on destination and coin availability.
    /// </summary>
    public SpendType SpendType { get; set; }

    /// <summary>
    /// Warning message if selection is suboptimal.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>
    /// Error if no valid selection possible.
    /// </summary>
    public string? Error { get; set; }
}
