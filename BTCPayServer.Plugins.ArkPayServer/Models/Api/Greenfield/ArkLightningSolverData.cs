namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// The Arkade swap solver a store's Lightning corridors run through.
/// </summary>
/// <remarks>
/// Carries no limits or fees, unlike the Boltz-era shape it replaces. A solver quotes its terms per
/// request over RFQ rather than publishing them, so any figure here would have to come from a
/// negotiation opened to answer a read-only call — or be invented. It carries no direction flags
/// either: both corridors work wherever a solver does, so the endpoint answering at all is the
/// answer, and a pair of constants would only look like they might one day differ.
/// </remarks>
public class ArkLightningSolverData
{
    /// <summary>The Nostr relay the solver is reached on.</summary>
    public string? RelayUri { get; set; }

    /// <summary>The solver's x-only public key, hex — its identity on the relay.</summary>
    public string? SolverPubkey { get; set; }
}
