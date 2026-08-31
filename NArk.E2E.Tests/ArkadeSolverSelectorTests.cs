using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.ArkadeIntents.Services;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// The plugin's solver selection, run against the registry that is actually published.
/// </summary>
/// <remarks>
/// Carries the <c>SolverRegistry</c> category so CI skips it: it reaches the public internet, and a
/// slow GitHub Pages or a delisted solver is not a reason to fail a build. Run it by category when
/// changing selection, or to answer "can this plugin find and dial a Lightning solver right now".
/// </remarks>
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
    public void A_hosted_network_ships_its_emulator_endpoint(string network)
    {
        // HasEmulator gates the whole corridor registration, discovery included, so an empty preset
        // does not degrade anything gracefully — it turns Lightning off and says nothing.
        var chain = network == "mainnet"
            ? Bitcoin.Instance.Mainnet.ChainName
            : Bitcoin.Instance.Mutinynet.ChainName;

        Assert.True(ArkadeSolverOptions.ForNetwork(chain).HasEmulator);
    }

    [Fact]
    [Trait("Category", "SolverPresets")]
    public void Every_network_with_a_registry_can_reach_a_solver_unconfigured()
    {
        // The end of the chain the preset feeds: a stock install on a hosted network should find a
        // solver without anyone editing ark.json.
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
    public async Task Refuses_a_size_no_solver_advertises()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        Assert.Null(await selector.SelectAsync(100_000_000));
        Assert.False(await selector.ServesAsync(100_000_000));
    }

    [Fact]
    public async Task Reports_the_range_it_can_be_paid_in()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        var range = await selector.ServedRangeAsync();

        Assert.NotNull(range);
        Assert.True(range!.Value.Min > 0);
        Assert.True(range.Value.Max > range.Value.Min);
    }

    [Fact]
    public async Task A_named_solver_skips_discovery_entirely()
    {
        // The development case: a stack's solver mints a fresh identity per run, so no registry can
        // list it. Naming one has to work with no registry reachable at all.
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
        Assert.True(await selector.ServesAsync(100_000_000));
    }
}
