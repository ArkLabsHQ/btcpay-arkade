namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Contract (address derivation) details.
/// </summary>
public class ArkContractData
{
    public string Script { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string? ContractType { get; set; }
    public string? ActivityState { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public int VtxoCount { get; set; }
}
