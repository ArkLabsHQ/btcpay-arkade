using BTCPayServer.Abstractions.Services;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Lightning;
using LNURL;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Holds a store's LNURL-pay offer to what its Arkade Lightning corridor can actually settle.
/// </summary>
/// <remarks>
/// <para>
/// LNURL is a standing offer to be paid any amount within a range, which makes an unsettleable
/// corridor worse here than elsewhere: the payer picks the amount and pays before anything of ours
/// gets a say, so a corridor that cannot fill leaves them holding a payment nothing will honour.
/// </para>
/// <para>
/// So the range is narrowed to what a solver publicly commits to serving, and the offer is
/// withdrawn outright when nothing can serve it at all. Both answers come from published cards
/// rather than from a negotiation — an LNURL request has no amount yet to negotiate over, and
/// opening a quote to answer one would mean quoting for every wallet that merely looks.
/// </para>
/// <para>
/// A solver named in configuration publishes no card, so there is nothing to narrow to and its
/// range is left alone. That is the development case, and the case of an operator who has already
/// decided who they trade with.
/// </para>
/// </remarks>
public class ArkadeLNURLPayRequestFilter(
    ArkadeLightningAvailabilityService availability,
    ArkadeSolverService solver
) : PluginHookFilter<StoreLNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    public override async Task<StoreLNURLPayRequest> Execute(StoreLNURLPayRequest request)
    {
        if (request?.Tag != "payRequest" || request.Store == null)
            return request;

        // Not using Arkade Lightning, so this store's LNURL is somebody else's problem.
        if (!availability.IsStoreUsingArkadeLightning(request.Store))
            return request;

        if (!solver.IsConfigured)
            return null!;

        if (await solver.ServedRangeAsync() is not { } served)
            return request;

        var min = LightMoney.Satoshis(served.Min);
        var max = LightMoney.Satoshis(served.Max);

        request.MinSendable = request.MinSendable > min ? request.MinSendable : min;
        request.MaxSendable = request.MaxSendable < max ? request.MaxSendable : max;

        // The store's own range and the corridor's can fail to overlap — a minimum above every
        // solver's maximum, or the reverse. An inverted range is not an offer anyone can take, and
        // publishing one invites a payment that cannot settle.
        return request.MinSendable > request.MaxSendable ? null! : request;
    }
}
