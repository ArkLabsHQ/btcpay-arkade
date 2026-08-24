using NArk.ArkadeIntents.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// The store's Arkade Lightning swaps, for the page that lists them.
/// </summary>
/// <remarks>
/// Separate from <see cref="StoreSwapsViewModel"/> rather than folded into it. They look alike from a
/// distance — both are swaps with a status and an amount — but a Boltz swap is history now and an
/// intent corridor is the live payment path, and the two answer different questions. A merchant
/// reading this page wants to know whether today's money arrived; a merchant reading the other one
/// wants to know what is left over from before.
/// </remarks>
public class StoreLightningSwapsViewModel : StoreCollectionViewModelBase
{
    /// <summary>The swaps, newest first.</summary>
    public IReadOnlyCollection<ArkadeSwapIntent> Swaps { get; set; } = [];

    /// <summary>Whether a solver is configured at all — the page says so when none is.</summary>
    public bool SolverConfigured { get; set; }

    /// <inheritdoc />
    public override int CurrentPageCount => Swaps.Count;
}
