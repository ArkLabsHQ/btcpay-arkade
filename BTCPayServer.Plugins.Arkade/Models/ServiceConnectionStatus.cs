namespace BTCPayServer.Plugins.Arkade.Models;

/// <summary>
/// Represents the connection status of an external service
/// </summary>
public class ServiceConnectionStatus
{
    public string? Url { get; set; }
    public bool IsConnected { get; set; }
    public string? Error { get; set; }

    public static ServiceConnectionStatus Connected(string? url) => new()
    {
        Url = url,
        IsConnected = true
    };

    public static ServiceConnectionStatus Disconnected(string? url, string? error = null) => new()
    {
        Url = url,
        IsConnected = false,
        Error = error
    };
}
