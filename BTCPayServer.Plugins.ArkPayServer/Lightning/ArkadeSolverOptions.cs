using System.Text.Json.Serialization;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// What this deployment needs to trade the Arkade Lightning corridors: a solver to trade with, and
/// the endpoints the corridors are built against.
/// </summary>
/// <remarks>
/// <para>
/// Read from the same <c>ark.json</c> the network endpoints come from, so an operator configures
/// Arkade in one file rather than two.
/// </para>
/// <para>
/// One solver, named outright. Solvers do publish cards to a per-network registry, but an indexed
/// market carries only who trades and what — not the relays to reach them on — so the registry cannot
/// open a transport by itself yet. Naming one makes the corridors work today and leaves discovery to
/// be switched on when a card carries enough to dial.
/// </para>
/// </remarks>
public class ArkadeSolverOptions
{
    /// <summary>The Nostr relay to reach the solver on. Both sides dial out; neither listens.</summary>
    [JsonPropertyName("solver-relay")]
    public string? RelayUri { get; set; }

    /// <summary>The solver's x-only public key, hex — its identity on the relay.</summary>
    [JsonPropertyName("solver-pubkey")]
    public string? SolverPubkey { get; set; }

    /// <summary>
    /// The claim daemon a receive swap seals its preimage to.
    /// </summary>
    /// <remarks>
    /// Needed for receiving only. The corridor's wire schema requires the sealed packet and no solver
    /// card carries the key to seal it with, so without this a deployment can pay Lightning invoices
    /// but cannot be paid over Lightning.
    /// </remarks>
    [JsonPropertyName("covclaimd")]
    public string? CovclaimdUri { get; set; }

    /// <summary>
    /// The covenant emulator that co-signs both corridors' scripts.
    /// </summary>
    /// <remarks>
    /// Not about the solver, but required by the same corridors and read from the same file. Its key
    /// is one of the parameters the lockup script commits to, so without it neither corridor can
    /// derive an address at all — this is the one endpoint whose absence disables sending as well as
    /// receiving.
    /// </remarks>
    [JsonPropertyName("emulator")]
    public string? EmulatorUri { get; set; }

    /// <summary>Whether the corridors can be built at all.</summary>
    /// <remarks>
    /// The emulator alone, deliberately. A solver can be missing and the plugin still starts with
    /// the corridors dark; an emulator can be missing and the DI graph would still be asked for an
    /// <c>IEmulatorProvider</c> the moment anything touched a covenant.
    /// </remarks>
    public bool HasEmulator => !string.IsNullOrWhiteSpace(EmulatorUri);

    /// <summary>Defaults for a network, where the endpoints are predictable.</summary>
    /// <param name="network">The chain this deployment runs on.</param>
    /// <returns>Options with whatever is knowable in advance filled in.</returns>
    /// <remarks>
    /// Only regtest gets defaults, and only the endpoints the arkade-regtest stack fixes.
    /// <see cref="SolverPubkey"/> is deliberately absent even there: a development solver mints a
    /// fresh identity per run, so it is the one value no default can know.
    /// </remarks>
    public static ArkadeSolverOptions ForNetwork(ChainName network) =>
        network == ChainName.Regtest
            ? new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                CovclaimdUri = "http://localhost:7271",
                EmulatorUri = "http://localhost:7073",
            }
            : new ArkadeSolverOptions();

    /// <summary>Overlay a file's values on a preset, field by field.</summary>
    /// <param name="preset">What <see cref="ForNetwork"/> knew in advance.</param>
    /// <param name="file">What the operator wrote, or <c>null</c> when the file has no solver block.</param>
    /// <returns>The merged options.</returns>
    /// <remarks>
    /// Per field rather than wholesale, so an operator who sets only <c>solver-pubkey</c> on regtest
    /// keeps the preset's relay and emulator instead of silently losing both.
    /// </remarks>
    public static ArkadeSolverOptions Merge(ArkadeSolverOptions preset, ArkadeSolverOptions? file) =>
        new()
        {
            RelayUri = Pick(file?.RelayUri, preset.RelayUri),
            SolverPubkey = Pick(file?.SolverPubkey, preset.SolverPubkey),
            CovclaimdUri = Pick(file?.CovclaimdUri, preset.CovclaimdUri),
            EmulatorUri = Pick(file?.EmulatorUri, preset.EmulatorUri),
        };

    private static string? Pick(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
