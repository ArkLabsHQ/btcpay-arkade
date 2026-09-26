using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Xunit;

namespace NArk.Tests;

public class ArkadeSolverConfigTests
{
    private static ArkadeSolverSelector Selector(string? relay, string? pubkey) =>
        new(new ArkadeSolverOptions { RelayUri = relay, SolverPubkey = pubkey }, networkName: null);

    [Fact]
    public async Task AnHttpSolverNeedsNoKey()
    {
        // A relay carries everyone's traffic and the key picks one counterparty out; an http endpoint
        // is already one, so requiring a key there only keeps a working solver from being used.
        var selector = Selector("http://127.0.0.1:8787", pubkey: null);

        Assert.True(selector.HasExplicitSolver);
        Assert.Equal("http://127.0.0.1:8787/", (await selector.SelectAsync(10_000))!.Relay.ToString());
    }

    [Fact]
    public void ARelayWithoutAKeyIsNotASolver()
    {
        Assert.False(Selector("ws://localhost:7777", pubkey: null).HasExplicitSolver);
        Assert.True(Selector("ws://localhost:7777", pubkey: new string('a', 64)).HasExplicitSolver);
    }
}
