namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Swap details (Lightning or chain swap via Boltz).
/// </summary>
public class ArkSwapData
{
    public string SwapId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public long AmountSats { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}
