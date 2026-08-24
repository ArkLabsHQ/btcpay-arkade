using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// What a wallet still has stuck in a Boltz swap, and what can be done about it.
/// </summary>
/// <param name="WalletId">The wallet holding it.</param>
/// <param name="SwapId">The swap.</param>
/// <param name="Type">Which corridor it was.</param>
/// <param name="AmountSats">What the swap was for.</param>
/// <param name="Recourse">How, or whether, the sats can be recovered.</param>
public record StrandedSwap(
    string WalletId,
    string SwapId,
    ArkSwapType Type,
    long AmountSats,
    SwapRecourse Recourse);

/// <summary>How a stranded swap can be recovered, if at all.</summary>
public enum SwapRecourse
{
    /// <summary>Its coins are on Arkade and the sweeper can take them. Nothing to do but wait.</summary>
    SweepableOnArkade,

    /// <summary>
    /// The swap is recorded but holds nothing: settled or refunded without the row being updated.
    /// </summary>
    NothingLeft,

    /// <summary>
    /// Its exposure is a Bitcoin HTLC, which this plugin cannot spend. Needs a human, and Boltz.
    /// </summary>
    OnchainNeedsOperator,
}

/// <summary>
/// Finds Boltz swaps left unresolved by the move to the Arkade intent corridors, and gets what it
/// can back.
/// </summary>
/// <remarks>
/// <para>
/// A migration, and it runs because this plugin cannot ask whether anybody needs it. The deployment
/// is self-hosted and there is no telemetry, so "probably nobody has stranded swaps" is not a fact
/// anyone here can establish — the only safe assumption is that somebody does, and that they will
/// never find out unless told.
/// </para>
/// <para>
/// <b>What it actually does, and does not.</b> It does not build refund transactions; the machinery
/// for that already exists and is better tested than anything written here would be. What it does is
/// make sure that machinery can see the money: a VHTLC whose contract went inactive is invisible to
/// <c>SweeperService</c>, and reactivating it is the difference between coins that come back and
/// coins that sit there. Where nothing is left it closes the row, so the count means something.
/// </para>
/// <para>
/// <b>Chain swaps it cannot help with, and says so.</b> Their exposure is a Bitcoin HTLC rather than
/// a VTXO, which the sweeper cannot see at all — it works on Arkade coins. Recovering one needs the
/// Boltz lockup transaction, and today that is fetched from Boltz's own API. So those are reported
/// rather than fixed, which is worth more than silence: a merchant who knows the sats exist can go
/// and get them, and one who does not, cannot.
/// </para>
/// </remarks>
public class ArkadeBoltzDrainService(
    ISwapStorage swapStorage,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    ILogger<ArkadeBoltzDrainService> logger) : BackgroundService
{
    /// <summary>Statuses that may still be holding money.</summary>
    /// <remarks>
    /// <c>Failed</c> belongs here despite the name: a swap that failed is one whose funding may well
    /// have landed before it did, and those sats are exactly the ones worth chasing.
    /// </remarks>
    private static readonly ArkSwapStatus[] Unresolved =
        [ArkSwapStatus.Pending, ArkSwapStatus.Unknown, ArkSwapStatus.Failed];

    /// <summary>What the last pass found, for anything that wants to show it.</summary>
    public IReadOnlyCollection<StrandedSwap> Stranded { get; private set; } = [];

    /// <inheritdoc />
    /// <remarks>
    /// Runs once at startup and then rarely. This is a migration, not a monitor — the sweeper does
    /// the watching, and a contract only goes inactive once.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A migration that throws on startup would take the plugin with it, and the swaps it
                // was going to rescue would be worse off for that than for one skipped pass.
                logger.LogError(e, "Boltz drain pass failed; retrying at the next interval");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    /// <summary>One pass over every unresolved swap.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What it found.</returns>
    public async Task<IReadOnlyCollection<StrandedSwap>> DrainAsync(CancellationToken cancellationToken = default)
    {
        var swaps = await swapStorage.GetSwaps(status: Unresolved, cancellationToken: cancellationToken);
        if (swaps.Count == 0)
        {
            Stranded = [];
            return Stranded;
        }

        var found = new List<StrandedSwap>();

        foreach (var swap in swaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            found.Add(await ExamineAsync(swap, cancellationToken));
        }

        Stranded = found;
        Report(found);
        return Stranded;
    }

    /// <summary>Works out what a single swap is holding, and reactivates its contract if it is.</summary>
    private async Task<StrandedSwap> ExamineAsync(ArkSwap swap, CancellationToken cancellationToken)
    {
        // A chain swap's money may be on Bitcoin rather than Arkade, and nothing here can spend that.
        // Judged on the metadata it was created with rather than on its VTXOs, since the absence of a
        // VTXO is exactly what an unrecovered onchain lockup looks like.
        if (swap.SwapType is ArkSwapType.ChainBtcToArk or ArkSwapType.ChainArkToBtc
            && swap.Metadata?.ContainsKey(SwapMetadata.BtcAddress) is true)
        {
            return new StrandedSwap(
                swap.WalletId, swap.SwapId, swap.SwapType, swap.ExpectedAmount,
                SwapRecourse.OnchainNeedsOperator);
        }

        var vtxos = await vtxoStorage.GetVtxos(
            scripts: [swap.ContractScript], cancellationToken: cancellationToken);

        var unspent = vtxos.Where(v => !v.IsSpent()).ToList();
        if (unspent.Count == 0)
        {
            return new StrandedSwap(
                swap.WalletId, swap.SwapId, swap.SwapType, swap.ExpectedAmount,
                SwapRecourse.NothingLeft);
        }

        // The one repair worth making. An inactive contract is not offered to the sweeper, so its
        // coins are unreachable however spendable the script itself is.
        var contracts = await contractStorage.GetContracts(
            scripts: [swap.ContractScript], cancellationToken: cancellationToken);

        foreach (var contract in contracts.Where(c => c.ActivityState != ContractActivityState.Active))
        {
            await contractStorage.UpdateContractActivityState(
                contract.WalletIdentifier, swap.ContractScript, ContractActivityState.Active, cancellationToken);

            logger.LogInformation(
                "Reactivated the contract for Boltz swap {SwapId}: it still holds {Count} coin(s) and " +
                "was not being swept", swap.SwapId, unspent.Count);
        }

        return new StrandedSwap(
            swap.WalletId, swap.SwapId, swap.SwapType, swap.ExpectedAmount,
            SwapRecourse.SweepableOnArkade);
    }

    /// <summary>Says what was found, once per pass, at a level matching what it means.</summary>
    private void Report(IReadOnlyCollection<StrandedSwap> found)
    {
        var onchain = found.Where(s => s.Recourse == SwapRecourse.OnchainNeedsOperator).ToList();
        var sweepable = found.Count(s => s.Recourse == SwapRecourse.SweepableOnArkade);

        if (sweepable > 0)
        {
            logger.LogInformation(
                "{Count} Boltz swap(s) still hold coins on Arkade; the sweeper will take them as each " +
                "becomes spendable.", sweepable);
        }

        // Warned rather than logged, and repeated every pass: this is the one nothing here will fix,
        // and it stays true until somebody acts on it.
        foreach (var swap in onchain)
        {
            logger.LogWarning(
                "Boltz swap {SwapId} ({Type}, {Amount} sats, wallet {WalletId}) has an onchain HTLC " +
                "this plugin cannot spend. Recovering it needs the Boltz lockup transaction. The " +
                "swap's own record holds the refund key, the lockup script and the address it was " +
                "paid to, so the sats are recoverable — but not from here.",
                swap.SwapId, swap.Type, swap.AmountSats, swap.WalletId);
        }
    }
}
