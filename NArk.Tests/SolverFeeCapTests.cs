using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.ArkadeIntents.SolverRegistry;
using Xunit;

namespace NArk.Tests;

public class SolverFeeCapTests
{
    private static readonly ArkadeSolverOptions Defaults = new();

    [Fact]
    public void DefaultCap_IsBpsOfTheDepositPlusFlat()
    {
        Assert.Equal(10_000 + 500, Defaults.MaxFeeOn(1_000_000));
        Assert.Equal(250 + 500, Defaults.MaxFeeOn(25_000));
    }

    [Fact]
    public async Task QuoteWithinCap_PassesThrough()
    {
        var inner = new FakeTransport(fromSats: 25_000, toSats: 24_925);
        using var transport = new FeeCappedRfqTransport(inner, Defaults.MaxFeeOn);

        var quote = await transport.RequestQuoteAsync<object, object>(null!);

        Assert.Equal(24_925, quote.ToAmount);
    }

    [Fact]
    public async Task QuoteAboveCap_IsRefused()
    {
        var inner = new FakeTransport(fromSats: 25_000, toSats: 1);
        using var transport = new FeeCappedRfqTransport(inner, Defaults.MaxFeeOn);

        await Assert.ThrowsAsync<SolverFeeTooHighException>(() =>
            transport.RequestQuoteAsync<object, object>(null!));
    }

    [Fact]
    public async Task QuoteWithoutPair_IsStillCapped()
    {
        var inner = new FakeTransport(fromSats: 100_000, toSats: 50_000, pair: null);
        using var transport = new FeeCappedRfqTransport(inner, Defaults.MaxFeeOn);

        await Assert.ThrowsAsync<SolverFeeTooHighException>(() =>
            transport.RequestQuoteAsync<object, object>(null!));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25_000, 0)]
    [InlineData(-1, 1)]
    public void NonPositiveLeg_IsRefused(long fromSats, long toSats)
    {
        Assert.Throws<SolverFeeTooHighException>(() =>
            FeeCappedRfqTransport.AssertFeeWithinCap(fromSats, toSats, Defaults.MaxFeeOn));
    }

    [Fact]
    public void Dispose_DisposesTheInnerTransport()
    {
        var inner = new FakeTransport(1, 1);

        new FeeCappedRfqTransport(inner, Defaults.MaxFeeOn).Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Merge_KeepsPresetCapsUnlessTheFileSetsThem()
    {
        var preset = new ArkadeSolverOptions { MaxFeeBps = 50, MaxFeeFlatSats = 100 };

        var untouched = ArkadeSolverOptions.Merge(preset, new ArkadeSolverOptions());
        Assert.Equal(50, untouched.MaxFeeBps);
        Assert.Equal(100, untouched.MaxFeeFlatSats);

        var overridden = ArkadeSolverOptions.Merge(preset, new ArkadeSolverOptions { MaxFeeBps = 0 });
        Assert.Equal(0, overridden.MaxFeeBps);
        Assert.Equal(100, overridden.MaxFeeFlatSats);
    }

    [Fact]
    public void Rendezvous_CarriesItsMarketAsACard()
    {
        var market = new IndexedMarket
        {
            Solver = "solver-a",
            DiscoveryPubkey = "ab",
            BaseAsset = new AssetDescriptor { Id = "btc" },
            QuoteAsset = new AssetDescriptor { Id = "btc" },
        };

        var card = new SolverRendezvous("ab", new Uri("wss://relay"), market).Card;

        Assert.NotNull(card);
        Assert.Equal("solver-a", card.Name);
        Assert.Same(market, Assert.Single(card.Markets));
        Assert.Null(new SolverRendezvous("ab", new Uri("wss://relay"), null).Card);
    }

    private sealed class FakeTransport(long fromSats, long toSats, string? pair = "lightning:BTC->arkade:BTC")
        : IRfqTransport, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
            RfqRequest<TRequestProfile> request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RfqQuote<TQuoteProfile>
            {
                SolverPubkey = "ab",
                Pair = pair,
                FromAmount = fromSats,
                ToAmount = toSats,
            });

        public Task<RfqQuote<EvmSendQuoteProfile>> RequestEvmSendQuoteAsync(
            EvmSendRfqRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(
            string rfqId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }
}
