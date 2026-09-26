using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public sealed class SolverFeeTooHighException(string message) : InvalidOperationException(message);

// Checked where the quote arrives because the SDK funds a send lockup in the same call that negotiates it,
// so the plugin never sees the quote before the irreversible step.
public sealed class FeeCappedRfqTransport(IRfqTransport inner, Func<long, long> maxFeeOn)
    : IRfqTransport, IDisposable
{
    public async Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
        RfqRequest<TRequestProfile> request, CancellationToken cancellationToken = default)
    {
        var quote = await inner.RequestQuoteAsync<TRequestProfile, TQuoteProfile>(request, cancellationToken);
        AssertFeeWithinCap(quote.FromAmount, quote.ToAmount, maxFeeOn);
        return quote;
    }

    public Task<RfqQuote<EvmSendQuoteProfile>> RequestEvmSendQuoteAsync(
        EvmSendRfqRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The EVM corridor is not offered by this plugin.");

    public Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(
        string rfqId, CancellationToken cancellationToken = default) =>
        inner.GetStatusAsync<TStatusProfile>(rfqId, cancellationToken);

    public void Dispose() => (inner as IDisposable)?.Dispose();

    public static void AssertFeeWithinCap(long fromSats, long toSats, Func<long, long> maxFeeOn)
    {
        if (fromSats <= 0 || toSats <= 0)
            throw new SolverFeeTooHighException(
                $"The solver quoted {fromSats} sats in for {toSats} sats out; refusing a non-positive leg.");

        var fee = fromSats - toSats;
        var cap = maxFeeOn(fromSats);
        if (fee > cap)
            throw new SolverFeeTooHighException(
                $"The solver charges {fee} sats on {fromSats}, above the {cap}-sat limit. " +
                "Raise max-fee-bps or max-fee-flat-sats in the Arkade network configuration to accept it.");
    }
}
