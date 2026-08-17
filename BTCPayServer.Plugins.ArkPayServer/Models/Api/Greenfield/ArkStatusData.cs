namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Overall Arkade service status for the store.
/// </summary>
public class ArkStatusData
{
    public bool IsConfigured { get; set; }
    public ArkServiceConnectionData ArkOperator { get; set; } = new();

    /// <summary>
    /// The Arkade swap solver this deployment trades Lightning corridors with.
    /// </summary>
    /// <remarks>
    /// <c>IsConnected</c> means configured, not reached. The RFQ transport has both sides dial out
    /// and neither listen, so there is nothing to ping — the first evidence a solver is really there
    /// is a quote coming back from it.
    /// </remarks>
    public ArkServiceConnectionData? Solver { get; set; }

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
