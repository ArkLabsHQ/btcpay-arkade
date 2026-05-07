using System.IO;

namespace BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;

/// <summary>
/// Per-wallet diagnostic log store. Captures structured log lines tagged
/// with a WalletId so the merchant can download a wallet-scoped log when
/// asking for support. Backed by a rolling file on disk in the plugin's
/// data directory.
/// </summary>
public interface IWalletLogStore
{
    /// <summary>
    /// Append a single pre-formatted log line to the given wallet's log.
    /// Non-blocking: implementations buffer and flush asynchronously, and
    /// drop oldest entries on backpressure rather than blocking the caller.
    /// </summary>
    void Append(string walletId, string line);

    /// <summary>
    /// Open the wallet's current log file (active rotation slot) for
    /// reading. Returns null when no log entries have been recorded yet.
    /// The caller owns the returned stream.
    /// </summary>
    Stream? OpenForRead(string walletId);
}
