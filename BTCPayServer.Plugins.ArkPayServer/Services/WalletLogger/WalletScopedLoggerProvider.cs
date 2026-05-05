using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;

/// <summary>
/// Logger provider that captures log entries from the NArk and Arkade
/// plugin categories and routes them to per-wallet log files when a
/// <c>WalletId</c> is found in the active scope chain or in the log
/// entry's structured state. Designed to live alongside whatever other
/// providers (console, file) BTCPay has configured — it doesn't replace
/// them, it adds a wallet-scoped sink.
/// </summary>
[ProviderAlias("ArkadeWalletScoped")]
public sealed class WalletScopedLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IWalletLogStore _store;
    private readonly ConcurrentDictionary<string, WalletScopedLogger> _loggers = new();
    private IExternalScopeProvider? _scopeProvider;

    public WalletScopedLoggerProvider(IWalletLogStore store)
    {
        _store = store;
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
        foreach (var logger in _loggers.Values) logger.SetScopeProvider(scopeProvider);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name =>
        {
            var logger = new WalletScopedLogger(name, _store);
            if (_scopeProvider is not null) logger.SetScopeProvider(_scopeProvider);
            return logger;
        });

    public void Dispose()
    {
        // Loggers hold no per-instance unmanaged state; the store is owned
        // by DI and will be disposed when the host service provider is.
    }
}
