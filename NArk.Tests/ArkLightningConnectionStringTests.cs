using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkLightningConnectionStringTests
{
    [Theory]
    [InlineData("type=arkade;wallet-id=wallet", "type=arkade;wallet-id=wallet")]
    [InlineData("type=arkade;wallet-id=wallet;store-id=store-A;spend-key=private-capability", "type=arkade;wallet-id=wallet;store-id=store-A")]
    public void ClientRetainsExplicitStoreIdentityWithoutExposingSpendCapability(string connectionString, string expected)
    {
        using var provider = ClientServices().BuildServiceProvider();
        var handler = new ArkLightningConnectionStringHandler(provider);

        var client = handler.Create(connectionString, Network.RegTest, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
        Assert.Equal(expected, client.ToString());
    }

    [Theory]
    [InlineData("type=arkade;wallet-id=wallet;store-id=")]
    [InlineData("type=arkade;wallet-id=wallet;store-id=private store")]
    public void MalformedStoreIdentityFailsWithoutEchoingInput(string connectionString)
    {
        using var provider = ClientServices().BuildServiceProvider();
        var handler = new ArkLightningConnectionStringHandler(provider);

        Assert.Null(handler.Create(connectionString, Network.RegTest, out var error));
        Assert.NotNull(error);
        Assert.DoesNotContain("private", error);
    }

    private static ServiceCollection ClientServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestProxy.Create<IClientTransport>());
        services.AddSingleton(TestProxy.Create<ISpendingService>());
        services.AddSingleton(TestProxy.Create<IBitcoinBlockchain>());
        services.AddSingleton<ILogger<ArkLightningInvoiceListener>>(NullLogger<ArkLightningInvoiceListener>.Instance);
        services.AddSingleton(new ArkLightningSpendKeyService(null!));
        return services;
    }
}
