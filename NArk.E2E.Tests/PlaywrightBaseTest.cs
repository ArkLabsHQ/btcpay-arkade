using BTCPayServer.Tests;
using Microsoft.Playwright;
using NBitcoin;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Base for Arkade plugin Playwright tests. Inherits BTCPay's
/// <see cref="UnitTestBase"/> so we can call <c>CreateServerTester</c>
/// via the shared <see cref="SharedPluginTestFixture"/>, then layers
/// per-test browser/page management on top.
///
/// Modelled directly on rockstardev/BTCPayServerPlugins.RockstarDev —
/// that repo is the canonical reference for running plugin E2E against
/// a real BTCPay process driven by BTCPay's own ServerTester.
/// </summary>
public abstract class PlaywrightBaseTest : UnitTestBase, IDisposable
{
    protected PlaywrightBaseTest(ITestOutputHelper helper) : base(helper)
    {
    }

    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }
    public IPage? Page { get; private set; }
    public Uri? ServerUri { get; private set; }
    public string? CreatedUser { get; private set; }
    public string? Password { get; private set; }

    private static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    /// <summary>Starts a Chromium browser and opens a page pointed at the running BTCPay.</summary>
    protected async Task InitializePlaywright(ServerTester serverTester)
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = true,
            SlowMo = IsRunningInCI ? 100 : 50
        };
        if (serverTester.PayTester.InContainer)
        {
            launchOptions.Args = new[]
            {
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-gpu"
            };
        }
        Browser = await Playwright.Chromium.LaunchAsync(launchOptions);

        var context = await Browser.NewContextAsync();
        Page = await context.NewPageAsync();
        Page.SetDefaultTimeout(15000);
        ServerUri = serverTester.PayTester.ServerUri;
        TestLogs.LogInformation($"Playwright: Browsing to {ServerUri}");
    }

    protected async Task GoToUrl(string relativeUrl)
    {
        ArgumentNullException.ThrowIfNull(Page);
        ArgumentNullException.ThrowIfNull(ServerUri);
        var trimmedBase = ServerUri.AbsoluteUri.TrimEnd('/');
        var trimmedRel = relativeUrl.StartsWith('/') ? relativeUrl : '/' + relativeUrl;
        await Page.GotoAsync(trimmedBase + trimmedRel);
    }

    /// <summary>Registers a new user via BTCPay's /register page. Mirrors BTCPay.Tests.PlaywrightTester.RegisterNewUser.</summary>
    protected async Task<string> RegisterNewUser(bool isAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(Page);

        var email = RandomUtils.GetUInt256().ToString().Substring(64 - 20) + "@a.com";
        await Page.FillAsync("#Email", email);
        await Page.FillAsync("#Password", "Passw0rd!");
        await Page.FillAsync("#ConfirmPassword", "Passw0rd!");
        if (isAdmin)
            await Page.ClickAsync("#IsAdmin");
        await Page.ClickAsync("#RegisterButton");

        CreatedUser = email;
        Password = "Passw0rd!";
        return email;
    }

    /// <summary>Creates a store via /stores/create. Matches BTCPay's #Create input + #Name field.</summary>
    protected async Task<string> CreateStore(string? name = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl("/stores/create");
        name ??= "ArkadeStore" + RandomUtils.GetUInt64();
        await Page.FillAsync("#Name", name);
        await Page.ClickAsync("#Create");
        // BTCPay redirects to /stores/{id}/dashboard or similar; the
        // store id is rendered in #Id on the General settings page.
        await Page.ClickAsync("#StoreNav-General");
        return await Page.InputValueAsync("#Id");
    }

    public void Dispose()
    {
        Try(() => { Page?.CloseAsync().GetAwaiter().GetResult(); Page = null; });
        Try(() => { Browser?.CloseAsync().GetAwaiter().GetResult(); Browser = null; });
        Try(() => { Playwright?.Dispose(); Playwright = null; });
        GC.SuppressFinalize(this);

        static void Try(Action a)
        {
            try { a(); } catch { /* test teardown: don't mask the real failure */ }
        }
    }
}
