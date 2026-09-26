using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using LNURL;
using Xunit;

namespace NArk.Tests;

public class ArkadeLNURLPayRequestFilterTests
{
    // Only a StoreLNURLPayRequest is ours to judge; the dependencies are never reached on these paths.
    private static readonly ArkadeLNURLPayRequestFilter Filter = new(null!, null!);

    [Fact]
    public async Task AnotherPluginsLightningAddressRequest_PassesThroughUntouched()
    {
        var request = new LNURLPayRequest { Tag = "payRequest" };

        Assert.Same(request, await Filter.Execute((object)request));
    }

    [Fact]
    public async Task AStoreRequestWithNoStore_PassesThroughUntouched()
    {
        var request = new StoreLNURLPayRequest { Tag = "payRequest" };

        Assert.Same(request, await Filter.Execute((object)request));
    }
}
