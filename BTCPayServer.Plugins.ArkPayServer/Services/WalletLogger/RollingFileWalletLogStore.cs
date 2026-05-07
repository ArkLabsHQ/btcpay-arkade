using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;

/// <summary>
/// File-backed implementation of <see cref="IWalletLogStore"/>.
/// One file per wallet at <c>{logDir}/{walletId}.log</c>, rotated
/// at 10 MB with up to 3 historical files preserved.
/// Writes are serialised through a single bounded channel so the calling
/// thread (which is usually a logger thread on a hot path) never blocks
/// on disk I/O. On backpressure the oldest pending lines are dropped.
/// </summary>
public sealed class RollingFileWalletLogStore : IWalletLogStore, IAsyncDisposable
{
    private const long MaxFileSizeBytes = 10L * 1024 * 1024;
    private const int RotatedFileCount = 3;
    private const int ChannelCapacity = 4096;

    private readonly string _logDir;
    private readonly ILogger<RollingFileWalletLogStore>? _logger;
    private readonly Channel<(string walletId, string line)> _channel;
    private readonly Task _writerTask;
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);
    private bool _disposed;

    public RollingFileWalletLogStore(string logDir, ILogger<RollingFileWalletLogStore>? logger = null)
    {
        _logDir = logDir;
        _logger = logger;
        Directory.CreateDirectory(_logDir);
        _channel = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public void Append(string walletId, string line)
    {
        if (_disposed || string.IsNullOrEmpty(walletId)) return;
        _channel.Writer.TryWrite((walletId, line));
    }

    public Stream? OpenForRead(string walletId)
    {
        var path = LogPathFor(walletId);
        if (!File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private string LogPathFor(string walletId) =>
        Path.Combine(_logDir, Sanitize(walletId) + ".log");

    private static string Sanitize(string walletId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[walletId.Length];
        for (var i = 0; i < walletId.Length; i++)
            buf[i] = Array.IndexOf(invalid, walletId[i]) >= 0 ? '_' : walletId[i];
        return new string(buf);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var (walletId, line) in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    var writer = GetOrOpenWriter(walletId);
                    if (writer.BaseStream.Length >= MaxFileSizeBytes)
                    {
                        CloseWriter(walletId);
                        RotateLogFiles(LogPathFor(walletId));
                        writer = GetOrOpenWriter(walletId);
                    }
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to append wallet log line for {WalletId}", walletId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private StreamWriter GetOrOpenWriter(string walletId) =>
        _writers.GetOrAdd(walletId, id =>
        {
            var fs = new FileStream(LogPathFor(id), FileMode.Append, FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            return new StreamWriter(fs) { AutoFlush = false };
        });

    private void CloseWriter(string walletId)
    {
        if (_writers.TryRemove(walletId, out var writer))
        {
            try { writer.Dispose(); } catch { /* swallow */ }
        }
    }

    private static void RotateLogFiles(string path)
    {
        for (var i = RotatedFileCount; i > 1; i--)
        {
            var older = $"{path}.{i - 1}";
            var newer = $"{path}.{i}";
            if (File.Exists(newer)) File.Delete(newer);
            if (File.Exists(older)) File.Move(older, newer);
        }
        var first = $"{path}.1";
        if (File.Exists(first)) File.Delete(first);
        if (File.Exists(path)) File.Move(path, first);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        try { await _writerTask; } catch { /* swallow */ }
        foreach (var walletId in _writers.Keys) CloseWriter(walletId);
    }
}
