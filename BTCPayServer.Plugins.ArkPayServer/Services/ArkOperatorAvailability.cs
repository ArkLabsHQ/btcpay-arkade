using System;
using System.Net.Http;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Classifies exceptions/error strings that mean "the Arkade operator (arkd) is
/// currently unreachable" — gRPC transport faults, HTTP 5xx from the operator's
/// edge (e.g. a Cloudflare 5xx page), and raw socket/DNS/timeout failures — and maps
/// them to a single user-facing message. Used so plugin pages show a friendly notice
/// instead of a raw gRPC dump like
/// <c>Status(StatusCode="Unknown", Detail="Bad gRPC response. HTTP status code: 530")</c>.
/// </summary>
public static class ArkOperatorAvailability
{
    /// <summary>The single user-facing message shown whenever the operator is unreachable.</summary>
    public const string UnavailableMessage =
        "The Arkade operator is currently unavailable. Please try again in a few moments.";

    // Substrings that appear in operator-unreachable failures regardless of the concrete
    // exception type. Matched case-insensitively against the whole exception chain. Kept
    // deliberately specific so genuine application errors (bad input, validation, "not
    // found") are NOT swallowed as "operator down".
    private static readonly string[] UnavailableMarkers =
    [
        "bad grpc response",            // gRPC-over-HTTP got a non-gRPC body (e.g. a Cloudflare 5xx page)
        "statuscode=\"unavailable\"",   // RpcException: server unreachable
        "statuscode=\"deadlineexceeded\"",
        "http status code: 5",          // 5xx from the operator's edge (502/503/530/...)
        "connection refused",
        "actively refused",             // Windows: TCP connect failure
        "no connection could be made",
        "name or service not known",    // Linux DNS failure
        "no such host is known",        // Windows DNS failure
        "connection reset",
        "the operation has timed out",
        "request timed out",
    ];

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) indicates the Arkade
    /// operator is unreachable rather than a genuine application/validation error.
    /// </summary>
    public static bool IsUnavailable(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // Type-based signals. We match RpcException by name rather than referencing
            // Grpc.Core directly to avoid coupling the plugin's error helper to the gRPC
            // package. OperationCanceledException is deliberately NOT treated as
            // "operator down" — it is usually the caller/browser aborting the request.
            var typeName = current.GetType().FullName ?? "";
            if (typeName.Contains("RpcException", StringComparison.Ordinal) ||
                current is HttpRequestException ||
                current is SocketException ||
                current is TimeoutException)
            {
                return true;
            }

            if (IsUnavailable(current.Message))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when an already-stringified error message looks like an operator-unreachable
    /// failure. Used where only the message survived (e.g. a cached status string).
    /// </summary>
    public static bool IsUnavailable(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        foreach (var marker in UnavailableMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Returns the friendly <see cref="UnavailableMessage"/> when <paramref name="ex"/> is
    /// an operator-unreachable failure; otherwise the original error text, prefixed with
    /// <paramref name="fallbackPrefix"/> when one is supplied (so genuine errors keep their detail).
    /// </summary>
    public static string Describe(Exception ex, string? fallbackPrefix = null)
    {
        if (IsUnavailable(ex))
            return UnavailableMessage;

        return string.IsNullOrEmpty(fallbackPrefix) ? ex.Message : $"{fallbackPrefix}: {ex.Message}";
    }

    /// <summary>
    /// Returns the friendly <see cref="UnavailableMessage"/> when <paramref name="message"/>
    /// looks like an operator-unreachable failure; otherwise the original message unchanged.
    /// </summary>
    public static string? DescribeMessage(string? message) =>
        IsUnavailable(message) ? UnavailableMessage : message;
}
