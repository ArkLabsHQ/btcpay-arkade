using Xunit;

namespace NArk.E2E.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComposedEvmTestCollection :
    ICollectionFixture<ComposedEvmInfrastructureFixture>,
    ICollectionFixture<SharedPluginTestFixture>
{
    public const string Name = "Arkade Composed EVM Tests";
}

[Collection(ComposedEvmTestCollection.Name)]
[Trait("Category", "ComposedEvmInfrastructure")]
public sealed class ComposedEvmInfrastructureReadinessTests(
    ComposedEvmInfrastructureFixture infrastructure,
    SharedPluginTestFixture btcpay,
    ITestOutputHelper output) : PlaywrightBaseTest(output)
{
    [Fact]
    public async Task Real_stack_and_btcpay_api_are_ready()
    {
        Assert.SkipWhen(!infrastructure.IsEnabled,
            $"Set {ComposedEvmInfrastructureFixture.EnableVariable}=1 and provide the regtest root and secrets file.");
        await infrastructure.StartAsync(TestContext.Current.CancellationToken);
        btcpay.Initialize(this);
        await infrastructure.AssertBtCPayReadinessAsync(
            btcpay.ServerTester!.PayTester.HttpClient, TestContext.Current.CancellationToken);
    }
}
