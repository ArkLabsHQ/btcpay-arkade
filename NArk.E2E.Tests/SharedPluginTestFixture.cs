using BTCPayServer.Tests;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// xUnit collection fixture that owns the BTCPayServer lifecycle for the
/// suite. One <see cref="ServerTester"/> instance starts BTCPay (with the
/// Arkade plugin loaded via <c>appsettings.dev.json:DEBUG_PLUGINS</c>) and
/// is shared by every test in <see cref="PluginTestCollection"/>.
///
/// Pattern copied from rockstardev/BTCPayServerPlugins.RockstarDev — that
/// repo demonstrates the only known-working way to run plugin E2E against
/// a real BTCPay process: inherit BTCPay's own ServerTester rather than
/// rolling a custom host-spawn fixture.
/// </summary>
public class SharedPluginTestFixture : IDisposable
{
    public ServerTester? ServerTester { get; private set; }

    /// <summary>
    /// Called by every test's constructor; starts BTCPay once and reuses
    /// the same instance for every subsequent call.
    /// </summary>
    public void Initialize(PlaywrightBaseTest testInstance)
    {
        if (ServerTester is not null) return;

        var testDir = Path.Combine(Directory.GetCurrentDirectory(), "ArkadePluginTests");
        ServerTester = testInstance.CreateServerTester(testDir, newDb: true);
        // Load plugins into the default AssemblyLoadContext so plugin types
        // share identity with BTCPay's. BTCPay's own Tests project uses the
        // same flag for the same reason.
        ServerTester.PayTester.LoadPluginsInDefaultAssemblyContext = true;
        ServerTester.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        ServerTester?.Dispose();
        ServerTester = null;
    }
}

[CollectionDefinition("Arkade Plugin Tests")]
public class PluginTestCollection : ICollectionFixture<SharedPluginTestFixture>
{
    // Marker class — xUnit discovers [CollectionDefinition] + ICollectionFixture<>.
}
