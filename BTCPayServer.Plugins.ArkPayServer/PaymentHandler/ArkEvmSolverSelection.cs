using System.Text.RegularExpressions;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>Registry discovery or a nonsecret HTTP base URL / Nostr relay and optional identity pin.</summary>
/// <param name="Mode">Either registry or explicit.</param>
/// <param name="Endpoint">Explicit base URL or relay; base paths are public and must not contain credentials.</param>
/// <param name="SolverPubkey">Explicit solver x-only key, required for WS(S).</param>
/// <param name="DiscoveryPubkey">Optional registry card x-only identity pin.</param>
/// <remarks>Userinfo, query strings and fragments are rejected; base paths are preserved.</remarks>
public sealed record ArkEvmSolverSelection(string Mode, string? Endpoint = null, string? SolverPubkey = null,
    string? DiscoveryPubkey = null)
{
    private static readonly Regex PubkeyPattern = new("\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant);

    /// <summary>Validates transport/identity consistency and normalizes the endpoint and keys.</summary>
    public ArkEvmSolverSelection Validate()
    {
        if (Mode == "registry" && Endpoint is null && SolverPubkey is null && ValidOptionalPubkey(DiscoveryPubkey))
            return this with { DiscoveryPubkey = DiscoveryPubkey?.ToLowerInvariant() };
        if (Mode != "explicit" || DiscoveryPubkey is not null || !ValidOptionalPubkey(SolverPubkey) ||
            Endpoint is null || Endpoint.Length > 2048 || Endpoint.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            uri.Scheme is not ("http" or "https" or "ws" or "wss") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Scheme is "ws" or "wss" && SolverPubkey is null)
            throw new ArgumentException("Specify registry discovery or a credential-free solver endpoint with the required public key.");

        var endpoint = uri.AbsoluteUri;
        if (uri.Scheme is "http" or "https" && !uri.AbsolutePath.EndsWith('/')) endpoint += "/";
        return this with { Endpoint = endpoint, SolverPubkey = SolverPubkey?.ToLowerInvariant() };
    }

    private static bool ValidOptionalPubkey(string? value) => value is null ||
        PubkeyPattern.IsMatch(value) && ECXOnlyPubKey.TryCreate(Convert.FromHexString(value), out _);

    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmSolverSelection);
}
