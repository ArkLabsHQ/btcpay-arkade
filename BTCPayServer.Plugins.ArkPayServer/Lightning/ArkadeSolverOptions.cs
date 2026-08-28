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
/// Naming a solver is optional. A card in the per-network registry now carries both halves of the
/// rendezvous — the key to address and the relays to meet on — so one is discovered per trade unless
/// these say otherwise. See <see cref="ArkadeSolverSelector"/>.
/// </para>
/// </remarks>
public class ArkadeSolverOptions
{
    /// <summary>
    /// The Nostr relay to reach a named solver on. Both sides dial out; neither listens.
    /// </summary>
    /// <remarks>
    /// Only consulted alongside <see cref="SolverPubkey"/>: naming one half of a rendezvous says
    /// nothing, so a relay without a key falls through to discovery rather than half-applying.
    /// </remarks>
    [JsonPropertyName("solver-relay")]
    public string? RelayUri { get; set; }

    /// <summary>
    /// A named solver's x-only public key, hex — its identity on the relay.
    /// </summary>
    /// <remarks>
    /// Set this and <see cref="RelayUri"/> to pin one counterparty and skip the registry. That is
    /// how a development stack works — its solver mints a fresh identity per run, so nothing can
    /// list it — and how an operator pins a solver they have already chosen.
    /// </remarks>
    [JsonPropertyName("solver-pubkey")]
    public string? SolverPubkey { get; set; }

    /// <summary>
    /// The claim daemon a receive swap seals its preimage to.
    /// </summary>
    /// <remarks>
    /// Optional. The corridor's wire schema always carries a sealed packet, but who holds the key to
    /// open it is a deployment choice: with a daemon configured a second party can finish a claim
    /// while this server is down, and without one the claim is ours alone to make.
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
    /// <para>
    /// <see cref="EmulatorUri"/> is here to satisfy <see cref="HasEmulator"/>, which gates the whole
    /// corridor registration — solver discovery included. Leaving it empty on a hosted network means
    /// Lightning is dark out of the box and an operator has to write in an address that was never
    /// theirs to choose.
    /// </para>
    /// <para>
    /// That gate is wrong, and this is a stopgap around it. Nothing here dials an emulator: the
    /// corridor needs the co-signer's KEY, which the SDK pins per network in <c>EmulatorPubKeys</c>,
    /// and of the covenant's eight leaves the two needing the emulator's signature are pushed by a
    /// claim daemon and by the solver, never by us. The gate should ask whether a key is pinned for
    /// this network, at which point these addresses can go.
    /// </para>
    /// <para>
    /// Signet is absent deliberately rather than forgotten: it runs no emulator deployment, and the
    /// SDK pins no co-signer key for it either, so a covenant could not be derived there whatever
    /// address was configured.
    /// </para>
    /// <para>
    /// <see cref="SolverPubkey"/> is absent everywhere, regtest included. A development solver mints
    /// a fresh identity per run, so it is the one value no default can know — and the regtest
    /// registry is published but empty, so naming one there is still the only way in.
    /// </para>
    /// </remarks>
    public static ArkadeSolverOptions ForNetwork(ChainName network)
    {
        if (network == ChainName.Regtest)
        {
            return new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                CovclaimdUri = "http://localhost:7271",
                EmulatorUri = "http://localhost:7073",
            };
        }

        if (network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
        {
            return new ArkadeSolverOptions { EmulatorUri = "https://emulator.arkade.sh" };
        }

        if (network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
        {
            return new ArkadeSolverOptions { EmulatorUri = "https://emulator.mutinynet.arkade.sh" };
        }

        return new ArkadeSolverOptions();
    }

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
