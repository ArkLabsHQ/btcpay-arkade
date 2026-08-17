using BTCPayServer.Abstractions.Services;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Plugins.ArkPayServer.Lightning;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Withdraws LNURL-pay from a store whose Arkade Lightning has nothing to settle with.
/// </summary>
/// <remarks>
/// <para>
/// LNURL is a standing offer to be paid any amount within a range, which makes an unsettleable
/// corridor worse here than elsewhere: the payer picks the amount and pays before anything of ours
/// gets a say, so a corridor that cannot fill leaves them holding a payment nothing will honour.
/// Withdrawing the offer is the only honest answer.
/// </para>
/// <para>
/// It no longer narrows the range. The Boltz integration this replaced clamped
/// <c>MinSendable</c>/<c>MaxSendable</c> to published swap limits; an Arkade solver's limits come
/// back with a quote, so there is nothing to clamp to until a negotiation is already open. An amount
/// outside the solver's terms is refused at quoting time with its own reason, rather than silently
/// excluded from a range computed earlier.
/// </para>
/// </remarks>
public class ArkadeLNURLPayRequestFilter(
    ArkadeLightningLimitsService limitsService,
    ArkadeSolverService solver
) : PluginHookFilter<StoreLNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    public override Task<StoreLNURLPayRequest> Execute(StoreLNURLPayRequest request)
    {
        if (request?.Tag != "payRequest" || request.Store == null)
            return Task.FromResult(request);

        // Not using Arkade Lightning, so this store's LNURL is somebody else's problem.
        if (!limitsService.IsStoreUsingArkadeLightning(request.Store))
            return Task.FromResult(request);

        return Task.FromResult(solver.IsConfigured ? request : null);
    }
}
