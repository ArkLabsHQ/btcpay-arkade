namespace BTCPayServer.Plugins.Arkade.Models.Api;

/// <summary>
/// Pre-flight validation request before executing spend.
/// </summary>
public class ValidateSpendRequest
{
    public List<string> VtxoOutpoints { get; set; } = new();
    public List<ValidateSpendOutput> Outputs { get; set; } = new();
}

public class ValidateSpendOutput
{
    public string Destination { get; set; } = "";
    public long? AmountSats { get; set; }
}
