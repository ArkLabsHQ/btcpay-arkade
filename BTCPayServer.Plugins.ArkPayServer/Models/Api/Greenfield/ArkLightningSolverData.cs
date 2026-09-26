namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// The Arkade swap solver a store's Lightning corridors run through.
/// </summary>
/// <remarks>No limits or fees: a solver quotes terms per request, so any figure here would be invented.</remarks>
public class ArkLightningSolverData
{
    /// <summary>The Nostr relay the solver is reached on.</summary>
    public string? RelayUri { get; set; }

    public string? SolverPubkey { get; set; }
}
