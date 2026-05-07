using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;

/// <summary>
/// Logger created by <see cref="WalletScopedLoggerProvider"/>. Inspects
/// the active scope chain and the structured log state for a
/// <c>WalletId</c> property and, if found, appends a formatted line to
/// the corresponding wallet log via <see cref="IWalletLogStore"/>.
/// Entries without a discoverable wallet id are dropped — there is no
/// "shared" log bucket. Add <c>using (logger.BeginScope(("WalletId", id)))</c>
/// at call sites that don't already pass <c>{WalletId}</c> in their
/// message template if you want them captured.
/// </summary>
internal sealed class WalletScopedLogger : ILogger
{
    // Categories whose entries we capture. Anything else is ignored —
    // there's no value in surfacing BTCPay-core or framework noise in a
    // wallet diagnostic log.
    private static readonly string[] CapturedCategoryPrefixes =
    [
        "NArk.",
        "BTCPayServer.Plugins.ArkPayServer.",
    ];

    private readonly string _category;
    private readonly IWalletLogStore _store;
    private readonly bool _categoryCaptured;
    private IExternalScopeProvider? _scopeProvider;

    public WalletScopedLogger(string category, IWalletLogStore store)
    {
        _category = category;
        _store = store;
        _categoryCaptured = IsCategoryCaptured(category);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider?.Push(state);

    public bool IsEnabled(LogLevel logLevel) =>
        _categoryCaptured && logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var walletId = ResolveWalletId(state);
        if (walletId is null) return;

        var message = formatter(state, exception);
        var line = FormatLine(logLevel, _category, eventId, message, exception);
        _store.Append(walletId, line);
    }

    private string? ResolveWalletId<TState>(TState state)
    {
        // Scope is checked first because it's the explicit form
        // (`logger.BeginScope(("WalletId", id))`). If a call site sets
        // both a scope and a structured arg, the scope wins.
        var walker = new ScopeWalker();
        _scopeProvider?.ForEachScope(static (scope, w) =>
        {
            w.Result ??= ExtractWalletId(scope);
        }, walker);

        return walker.Result ?? ExtractWalletId(state);
    }

    private sealed class ScopeWalker { public string? Result { get; set; } }

    private static string? ExtractWalletId(object? source)
    {
        if (source is null) return null;

        if (source is IReadOnlyList<KeyValuePair<string, object?>> list)
        {
            foreach (var kv in list)
                if (IsWalletIdKey(kv.Key) && kv.Value is not null)
                    return kv.Value.ToString();
        }

        if (source is KeyValuePair<string, object?> single &&
            IsWalletIdKey(single.Key) && single.Value is not null)
            return single.Value.ToString();

        if (source is ValueTuple<string, string> stringTuple &&
            IsWalletIdKey(stringTuple.Item1))
            return stringTuple.Item2;

        if (source is ValueTuple<string, object> objectTuple &&
            IsWalletIdKey(objectTuple.Item1))
            return objectTuple.Item2.ToString();

        return null;
    }

    private static bool IsWalletIdKey(string key) =>
        string.Equals(key, "WalletId", StringComparison.Ordinal);

    private static bool IsCategoryCaptured(string category)
    {
        foreach (var prefix in CapturedCategoryPrefixes)
            if (category.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string FormatLine(LogLevel level, string category, EventId eventId,
        string message, Exception? exception)
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(256);
        sb.Append(ts).Append(' ');
        sb.Append(LevelTag(level)).Append(' ');
        sb.Append(category);
        if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
        {
            sb.Append('[').Append(eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name)) sb.Append(':').Append(eventId.Name);
            sb.Append(']');
        }
        sb.Append(": ").Append(message);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }
        return sb.ToString();
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
