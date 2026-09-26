using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.ArkadeIntents.Services;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

// SolverRegistry category so CI skips it: it reaches the public internet.
[Trait("Category", "SolverRegistry")]
public class ArkadeSolverSelectorTests
{
    private static ArkadeSolverSelector Selector(ChainName network) => new(
        new ArkadeSolverOptions(),
        ArkadeSolverSelector.RegistryNetworkName(network),
        new SolverDiscoveryService(new HttpClient()));

    [Theory]
    [InlineData("mainnet")]
    [InlineData("mutinynet")]
    [Trait("Category", "SolverPresets")]
    public void A_hosted_network_has_a_pinned_emulator_key(string network)
    {
        // Without one the corridors cannot build their covenants, so they do not register at all.
        var chain = network == "mainnet"
            ? Bitcoin.Instance.Mainnet.ChainName
            : Bitcoin.Instance.Mutinynet.ChainName;

        Assert.True(ArkadeSolverOptions.HasPinnedEmulatorKey(chain));
    }

    [Fact]
    [Trait("Category", "SolverPresets")]
    public void Every_network_with_a_registry_can_reach_a_solver_unconfigured()
    {
        foreach (var chain in new[] { Bitcoin.Instance.Mainnet.ChainName, Bitcoin.Instance.Mutinynet.ChainName })
        {
            var options = ArkadeSolverOptions.ForNetwork(chain);
            var selector = new ArkadeSolverSelector(
                options,
                ArkadeSolverSelector.RegistryNetworkName(chain),
                new SolverDiscoveryService(new HttpClient()));

            Assert.False(selector.HasExplicitSolver);
            Assert.True(selector.CanReachASolver, $"{chain} cannot reach a solver out of the box");
        }
    }

    [Fact]
    public async Task Selects_a_diallable_lightning_solver_on_mutinynet()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.NotNull(rendezvous);
        Assert.NotEmpty(rendezvous!.Pubkey);
        Assert.Contains(rendezvous.Relay.Scheme, new[] { "ws", "wss" });
        Assert.Equal("lightning", rendezvous.Market?.QuoteCorridor);
    }

    [Fact]
    public async Task Selects_regardless_of_size_and_leaves_the_bounds_to_the_quote()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        Assert.NotNull(await selector.SelectAsync(100_000_000));
        Assert.True(await selector.HasLightningSolverAsync());
    }

    [Fact]
    public async Task Reports_the_range_it_can_be_paid_in()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        var range = await selector.ServedRangeAsync();

        Assert.NotNull(range);
        Assert.True(range!.Value.Min > 0);
        Assert.True(range.Value.Max > range.Value.Min);

        var market = (await selector.SelectAsync(10_000))!.Market!;
        Assert.Equal(market.MinQuoteAmount, range.Value.Min);
        Assert.Equal(market.MaxQuoteAmount, range.Value.Max);
    }

    // The tests above depend on published data; these supply their own registry so selection is pinned.

    private static ArkadeSolverSelector SelectorOver(string marketsJson) => new(
        new ArkadeSolverOptions(),
        "mutinynet",
        new SolverDiscoveryService(new HttpClient(new CannedRegistry($$"""
        {
          "version": 0,
          "network": "mutinynet",
          "generated_at": 1783958400,
          "commit": "deadbeef",
          "markets": [ {{marketsJson}} ]
        }
        """))));

    private static string LightningMarket(
        string solver, int feeBps, string feeFlat = "0",
        string minQuote = "1000", string maxQuote = "5000000",
        string minBase = "1000", string maxBase = "5000000",
        string corridor = ArkadeSolverSelector.LightningCorridor) => $$"""
        {
          "pair": "BTC/BTC",
          "solver": "{{solver}}",
          "discovery_pubkey": "{{solver}}-pubkey",
          "base_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_corridor": "{{corridor}}",
          "fee_bps": {{feeBps}},
          "fee_flat": "{{feeFlat}}",
          "min_base_amount": "{{minBase}}",
          "max_base_amount": "{{maxBase}}",
          "min_quote_amount": "{{minQuote}}",
          "max_quote_amount": "{{maxQuote}}",
          "transports": { "nostr": { "relays": ["wss://relay.example"] } }
        }
        """;

    // The index shape of a CAIP-19 card: the reducer down-projects id and carries the real identity in caip19_id.
    private static string CanonicalMarket(
        string solver, int feeBps, string baseId = "arkade:mutinynet/slip44:1",
        string quoteId = "bolt11:mutinynet/slip44:1") => $$"""
        {
          "pair": "BTC/lightning:BTC",
          "solver": "{{solver}}",
          "discovery_pubkey": "{{solver}}-pubkey",
          "base_asset": { "id": "btc", "caip19_id": "{{baseId}}", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_asset": { "id": "btc", "caip19_id": "{{quoteId}}", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "fee_bps": {{feeBps}},
          "min_base_amount": "1000",
          "max_base_amount": "5000000",
          "min_quote_amount": "1000",
          "max_quote_amount": "5000000",
          "transports": { "nostr": { "relays": ["wss://relay.example"] } }
        }
        """;

    private const string UsdtOnArkade =
        "arkade:mutinynet/asset:f121ac9b7656797cc68d1e8fecacfbaa2069ec1461edf0bf2f3c37404cb9791a0000";

    [Fact]
    public async Task A_caip19_card_serves_its_corridor()
    {
        var selector = SelectorOver(string.Join(",",
            CanonicalMarket("ln-solver", feeBps: 10),
            CanonicalMarket("onchain-solver", feeBps: 10, quoteId: "bitcoin:mutinynet/slip44:1")));

        Assert.Equal("ln-solver-pubkey", (await selector.SelectAsync(10_000))!.Pubkey);
        Assert.Equal("onchain-solver-pubkey",
            (await selector.SelectAsync(10_000, ArkadeSolverSelector.OnchainCorridor))!.Pubkey);
        Assert.Equal((1000L, 5000000L), await selector.ServedRangeAsync());
    }

    [Fact]
    public async Task Legacy_and_caip19_cards_compete_on_price()
    {
        var selector = SelectorOver(string.Join(",",
            LightningMarket("legacy-solver", feeBps: 60),
            CanonicalMarket("caip19-solver", feeBps: 10)));

        Assert.Equal("caip19-solver-pubkey", (await selector.SelectAsync(10_000))!.Pubkey);
    }

    [Theory]
    [InlineData(UsdtOnArkade, "bolt11:mutinynet/slip44:1")]
    [InlineData(UsdtOnArkade, "bolt11:mutinynet/asset:f121ac9b7656797cc68d1e8fecacfbaa2069ec1461edf0bf2f3c37404cb9791a0000")]
    [InlineData("arkade:mutinynet/slip44:1", "eip155:1/slip44:60")]
    [InlineData("not-a-caip19-id/at-all", "bolt11:mutinynet/slip44:1")]
    public async Task A_market_that_is_not_bitcoin_both_ways_is_ignored(string baseId, string quoteId)
    {
        var selector = SelectorOver(CanonicalMarket("other", feeBps: 10, baseId, quoteId));

        Assert.False(await selector.HasSolverForAsync(ArkadeSolverSelector.LightningCorridor));
        Assert.Null(await selector.SelectAsync(10_000));
    }

    [Fact]
    public async Task Picks_the_market_that_costs_the_payer_least_at_this_size()
    {
        // At 10k sats the 10bps card's flat 500 makes it dearer than the 60bps card, so fee_bps alone would mis-rank.
        var selector = SelectorOver(string.Join(",",
            LightningMarket("flat-heavy", feeBps: 10, feeFlat: "500"),
            LightningMarket("spread-only", feeBps: 60)));

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.Equal("spread-only-pubkey", rendezvous!.Pubkey);
    }

    [Fact]
    public async Task The_cheapest_market_changes_with_the_size()
    {
        // At 1M sats the flat fee is noise and the ranking inverts; one size alone would pass an amount-blind impl.
        var selector = SelectorOver(string.Join(",",
            LightningMarket("flat-heavy", feeBps: 10, feeFlat: "500"),
            LightningMarket("spread-only", feeBps: 60)));

        var rendezvous = await selector.SelectAsync(1_000_000);

        Assert.Equal("flat-heavy-pubkey", rendezvous!.Pubkey);
    }

    [Fact]
    public async Task The_advertised_range_is_the_leg_the_payer_sends_on()
    {
        // Deliberately incompatible values so reading the wrong side can't coincide with the right one.
        var selector = SelectorOver(LightningMarket(
            "solo", feeBps: 10,
            minQuote: "2000", maxQuote: "300000",
            minBase: "7", maxBase: "9"));

        var range = await selector.ServedRangeAsync();

        Assert.Equal((2000L, 300000L), range);
    }

    [Fact]
    public async Task A_corridor_is_never_answered_with_another_ones_solver()
    {
        var selector = SelectorOver(string.Join(",",
            LightningMarket("ln-solver", feeBps: 10),
            LightningMarket("onchain-solver", feeBps: 90,
                corridor: ArkadeSolverSelector.OnchainCorridor)));

        var lightning = await selector.SelectAsync(10_000, ArkadeSolverSelector.LightningCorridor);
        var onchain = await selector.SelectAsync(10_000, ArkadeSolverSelector.OnchainCorridor);

        Assert.Equal("ln-solver-pubkey", lightning!.Pubkey);

        // The dearer card wins: it is the only one on this rail, so ranking can't be what picked it.
        Assert.Equal("onchain-solver-pubkey", onchain!.Pubkey);
    }

    [Fact]
    public async Task A_corridor_nobody_serves_has_no_solver()
    {
        var selector = SelectorOver(LightningMarket("ln-solver", feeBps: 10));

        Assert.True(await selector.HasSolverForAsync(ArkadeSolverSelector.LightningCorridor));
        Assert.False(await selector.HasSolverForAsync(ArkadeSolverSelector.OnchainCorridor));
        Assert.Null(await selector.SelectAsync(10_000, ArkadeSolverSelector.OnchainCorridor));
    }

    private sealed class CannedRegistry(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task A_named_solver_skips_discovery_entirely()
    {
        var selector = new ArkadeSolverSelector(
            new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                SolverPubkey = "3f831510a6d7678d0c90d7d6fbc4057720517e2e30681ef4c87cc57aaf57e8d5",
            },
            networkName: null);

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.NotNull(rendezvous);
        Assert.Equal("ws://localhost:7777/", rendezvous!.Relay.ToString());
        Assert.Null(rendezvous.Market);
        Assert.True(await selector.HasLightningSolverAsync());
        Assert.Null(await selector.ServedRangeAsync());
    }
}
