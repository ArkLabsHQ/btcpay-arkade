using System.Text.Json.Serialization;
using NArk.Arkade.Emulator;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Solver and endpoint settings for the Arkade Lightning corridors, read from <c>ark.json</c>.</summary>
public class ArkadeSolverOptions
{
    // Only consulted alongside SolverPubkey; a relay without a key falls through to discovery.
    [JsonPropertyName("solver-relay")]
    public string? RelayUri { get; set; }

    // Set with RelayUri to pin one solver and skip the registry (a dev solver mints a fresh identity per run).
    [JsonPropertyName("solver-pubkey")]
    public string? SolverPubkey { get; set; }

    // Optional: with a daemon a second party can finish a claim while this server is down.
    [JsonPropertyName("covclaimd")]
    public string? CovclaimdUri { get; set; }

    // Applies to every quote, carded or not: a named solver publishes no card, and the SDK's card check
    // is skipped when the quote's own pair field doesn't resolve.
    [JsonPropertyName("max-fee-bps")]
    public int? MaxFeeBps { get; set; }

    [JsonPropertyName("max-fee-flat-sats")]
    public long? MaxFeeFlatSats { get; set; }

    public const int DefaultMaxFeeBps = 100;
    public const long DefaultMaxFeeFlatSats = 500;

    // A pinned co-signer key is the whole requirement here: nothing in this plugin dials an emulator.
    public static bool HasPinnedEmulatorKey(ChainName network)
    {
        var name = NetworkName(network);
        if (name is null) return false;

        try
        {
            EmulatorPubKeys.DefaultFor(name);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? NetworkName(ChainName network) =>
        network == ChainName.Regtest ? "regtest"
        : network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName ? "bitcoin"
        : network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName ? "mutinynet"
        : null;

    public long MaxFeeOn(long depositedSats) =>
        depositedSats * (MaxFeeBps ?? DefaultMaxFeeBps) / 10_000 + (MaxFeeFlatSats ?? DefaultMaxFeeFlatSats);

    public static ArkadeSolverOptions ForNetwork(ChainName network)
    {
        if (network == ChainName.Regtest)
        {
            return new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                CovclaimdUri = "http://localhost:7271",
            };
        }

        return new ArkadeSolverOptions();
    }

    // Per field, so setting only solver-pubkey on regtest keeps the preset relay and emulator.
    public static ArkadeSolverOptions Merge(ArkadeSolverOptions preset, ArkadeSolverOptions? file) =>
        new()
        {
            RelayUri = Pick(file?.RelayUri, preset.RelayUri),
            SolverPubkey = Pick(file?.SolverPubkey, preset.SolverPubkey),
            CovclaimdUri = Pick(file?.CovclaimdUri, preset.CovclaimdUri),
            MaxFeeBps = file?.MaxFeeBps ?? preset.MaxFeeBps,
            MaxFeeFlatSats = file?.MaxFeeFlatSats ?? preset.MaxFeeFlatSats,
        };

    private static string? Pick(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
