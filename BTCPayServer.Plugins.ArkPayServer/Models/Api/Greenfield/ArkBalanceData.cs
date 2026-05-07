namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Balance breakdown in satoshis.
/// </summary>
public class ArkBalanceData
{
    /// <summary>
    /// Available balance (spendable now, excluding locked), in sats.
    /// </summary>
    public long AvailableSats { get; set; }

    /// <summary>
    /// Balance locked in pending intents (WaitingToSubmit/WaitingForBatch), in sats.
    /// </summary>
    public long LockedSats { get; set; }

    /// <summary>
    /// Balance in VTXOs nearing expiration (recoverable via unilateral exit), in sats.
    /// </summary>
    public long RecoverableSats { get; set; }

    /// <summary>
    /// VTXOs owned but not yet spendable (e.g., HTLC timelocks), in sats.
    /// </summary>
    public long UnspendableSats { get; set; }

    /// <summary>
    /// Balance currently boarding (onchain UTXOs not yet settled into Ark), in sats.
    /// </summary>
    public long BoardingSats { get; set; }

    /// <summary>
    /// Total balance (all categories combined).
    /// </summary>
    public long TotalSats => AvailableSats + LockedSats + RecoverableSats + UnspendableSats + BoardingSats;
}
