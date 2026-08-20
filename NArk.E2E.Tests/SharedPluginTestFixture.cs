using System.Text.Json;
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

        // Shorten the Arkade plugin's intent-generation poll cadence for
        // the suite. Production leaves this unset (NArk's 5-min default);
        // tests that fund a wallet by importing a note need the redemption
        // intent generated within seconds, not minutes. BTCPay's config
        // reads BTCPAY_-prefixed env vars, so this lands as
        // ARKINTENTPOLLSECONDS=5 in IConfiguration. Must be set before
        // ServerTester.StartAsync builds the host config.
        Environment.SetEnvironmentVariable("BTCPAY_ARKINTENTPOLLSECONDS", "5");

        var testDir = Path.Combine(Directory.GetCurrentDirectory(), "ArkadePluginTests");
        WriteSolverConfigIfRequested(testDir);
        ServerTester = testInstance.CreateServerTester(testDir, newDb: true);
        // Load plugins in an isolated AssemblyLoadContext — matches the
        // production load model AND matches rockstardev's reference
        // fixture.
        ServerTester.PayTester.LoadPluginsInDefaultAssemblyContext = false;

        // Hard 3-min ceiling on BTCPay startup. Past iterations of this
        // suite saw a 20-min CI timeout because BTCPay's host gets stuck
        // somewhere downstream of the plugin's Execute (likely a hosted
        // service / IStartupTask in NArk.Core). Failing fast surfaces
        // the real signal instead of timing the GitHub Actions job out.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            ServerTester.StartAsync().WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "BTCPay startup didn't complete within 3 minutes. The plugin's hosted services or IStartupTask are likely blocking. Run the test locally with debugger attached to inspect.");
        }
    }

    /// <summary>
    /// Points the plugin at a locally-run Arkade swap solver, when one was asked for.
    /// </summary>
    /// <param name="dataDir">BTCPay's data directory for this run — where the plugin reads its config.</param>
    /// <remarks>
    /// <para>
    /// Written as <c>ark.json</c> in the data directory because that is where the plugin actually
    /// reads it from in production; configuring it any other way here would test a path no
    /// deployment uses. Only the keys the corridors need are written — the plugin merges a partial
    /// file over its per-network preset, so the operator endpoints keep the regtest defaults.
    /// </para>
    /// <para>
    /// Gated on <see cref="SolverUrlVariable"/> being set, and so a no-op for the rest of the suite.
    /// The corridors need a solver, a claim daemon and a covenant emulator running alongside the
    /// regtest stack, none of which CI starts — see <c>ArkadeLightningCorridorTests</c> for how to
    /// bring them up.
    /// </para>
    /// </remarks>
    private static void WriteSolverConfigIfRequested(string dataDir)
    {
        var solverUrl = Environment.GetEnvironmentVariable(SolverUrlVariable);
        if (string.IsNullOrWhiteSpace(solverUrl)) return;

        Directory.CreateDirectory(dataDir);

        // An http(s) endpoint selects the HTTP transport; a ws(s) one selects the relay, which then
        // also needs the solver's key to address it on. Both are passed through as given.
        var config = new Dictionary<string, string?>
        {
            ["solver-relay"] = solverUrl,
            ["solver-pubkey"] = Environment.GetEnvironmentVariable("ARKADE_E2E_SOLVER_PUBKEY"),
            ["covclaimd"] = Environment.GetEnvironmentVariable("ARKADE_E2E_COVCLAIMD_URL")
                            ?? "http://localhost:7271",
            ["emulator"] = Environment.GetEnvironmentVariable("ARKADE_E2E_EMULATOR_URL")
                           ?? "http://localhost:7073",
        };

        var json = JsonSerializer.Serialize(
            config.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(dataDir, "ark.json"), json);
    }

    /// <summary>
    /// The environment variable naming the solver to trade with. Unset means the Lightning-corridor
    /// tests do not run.
    /// </summary>
    public const string SolverUrlVariable = "ARKADE_E2E_SOLVER_URL";

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
