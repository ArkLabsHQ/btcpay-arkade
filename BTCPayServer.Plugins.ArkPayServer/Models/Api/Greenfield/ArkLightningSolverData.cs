namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// The Arkade swap solver a store's Lightning corridors run through.
/// </summary>
/// <remarks>
/// Carries no limits or fees, unlike the Boltz-era shape it replaces. A solver quotes its terms per
/// request over RFQ rather than publishing them, so any figure here would have to come from a
/// negotiation opened to answer a read-only call — or be invented.
/// </remarks>
public class ArkLightningSolverData
{
    /// <summary>The Nostr relay the solver is reached on.</summary>
    public string? RelayUri { get; set; }

    /// <summary>The solver's x-only public key, hex — its identity on the relay.</summary>
    public string? SolverPubkey { get; set; }

    /// <summary>Whether this store can pay Lightning invoices out of its Arkade balance.</summary>
    public bool CanSend { get; set; }

    /// <summary>
    /// Whether this store can be paid over Lightning.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="CanSend"/> on purpose: receiving additionally needs a claim daemon,
    /// because the corridor seals its preimage to one and the solver refuses a request without it.
    /// </remarks>
    public bool CanReceive { get; set; }
}
