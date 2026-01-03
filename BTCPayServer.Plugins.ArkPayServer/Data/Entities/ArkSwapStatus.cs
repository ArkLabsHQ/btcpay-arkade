namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public enum ArkSwapStatus
{
    Pending,
    Settled,
    Failed,
    Refunded,
    Unknown
}

public static class ArkSwapStatusExtensions
{
    public static bool IsActive(this ArkSwapStatus status)
    {
        return status == ArkSwapStatus.Pending || status == ArkSwapStatus.Unknown;
    }

    public static bool IsCompleted(this ArkSwapStatus status)
    {
        return status == ArkSwapStatus.Settled ||
               status == ArkSwapStatus.Failed ||
               status == ArkSwapStatus.Refunded;
    }
}