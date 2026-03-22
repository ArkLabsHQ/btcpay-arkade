namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Ark operator server information.
/// </summary>
public class ArkServerInfoData
{
    public string Network { get; set; } = "";
    public long DustSats { get; set; }
    public string SignerPubKey { get; set; } = "";
    public int UnilateralExitBlocks { get; set; }
    public int BoardingExitBlocks { get; set; }
    public string? ForfeitAddress { get; set; }
}
