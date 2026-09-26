using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NArk.Tests;

public class ArkadeSolverClaimRecipientTests
{
    private static ArkadeSolverService Service(string? covclaimd)
    {
        var options = new ArkadeSolverOptions { CovclaimdUri = covclaimd };
        return new ArkadeSolverService(
            options, new ArkadeSolverSelector(options, networkName: null), new Factory(),
            NullLogger<ArkadeSolverService>.Instance);
    }

    [Fact]
    public async Task WithoutAClaimDaemon_NoPacketIsSealed()
    {
        Assert.Null(await Service(null).ResolveClaimRecipientAsync());
    }

    [Fact]
    public async Task AnUnreachableClaimDaemon_DoesNotFailThePayment()
    {
        Assert.Null(await Service("http://127.0.0.1:1").ResolveClaimRecipientAsync());
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
    }
}
