using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Lightning;
using LNURL;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

// LNURL payers pay before we get a say, so the range is narrowed to what published cards serve, and withdrawn
// when nothing can. A configured solver publishes no card and leaves the range alone.
// IPluginHookFilter rather than PluginHookFilter<T>: the typed base turns any other argument into null,
// which blanked the Lightning Address responses other plugins build as a plain LNURLPayRequest.
public class ArkadeLNURLPayRequestFilter(
    ArkadeLightningAvailabilityService availability,
    ArkadeSolverService solver
) : IPluginHookFilter
{
    public string Hook => "modify-lnurlp-request";

    public async Task<object> Execute(object args) =>
        args is StoreLNURLPayRequest request ? await Execute(request) : args;

    public async Task<StoreLNURLPayRequest> Execute(StoreLNURLPayRequest request)
    {
        if (request.Tag != "payRequest" || request.Store == null)
            return request;

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

        // The store's and corridor's ranges may not overlap; an inverted range invites an unsettleable payment.
        return request.MinSendable > request.MaxSendable ? null! : request;
    }
}
