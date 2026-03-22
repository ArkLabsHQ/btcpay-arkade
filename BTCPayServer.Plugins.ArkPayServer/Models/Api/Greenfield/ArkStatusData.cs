namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Overall Arkade service status for the store.
/// </summary>
public class ArkStatusData
{
    public bool IsConfigured { get; set; }
    public ArkServiceConnectionData ArkOperator { get; set; } = new();
    public ArkServiceConnectionData? Boltz { get; set; }
    public ArkBlockchainData? Blockchain { get; set; }
}

public class ArkServiceConnectionData
{
    public string? Url { get; set; }
    public bool IsConnected { get; set; }
    public string? Error { get; set; }
}

public class ArkBlockchainData
{
    public long Height { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
