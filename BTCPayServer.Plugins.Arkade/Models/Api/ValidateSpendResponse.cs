namespace BTCPayServer.Plugins.Arkade.Models.Api;

/// <summary>
/// Validation result with any errors found.
/// </summary>
public class ValidateSpendResponse
{
    public bool IsValid { get; set; }
    public SpendType? SpendType { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<OutputValidationResult> OutputResults { get; set; } = new();
}

public class OutputValidationResult
{
    public int Index { get; set; }
    public DestinationType? DetectedType { get; set; }
    public string? Error { get; set; }
}
