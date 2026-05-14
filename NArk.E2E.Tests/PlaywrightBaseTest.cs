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

        // BTCPay's Arkade overview page holds long-polling XHRs (VTXO
        // subscription, stream events) that saturate Chromium's 6-connection
        // per-origin pool. Subsequent same-origin navigations then hang
        // waiting for a connection slot. Routing through about:blank first
        // tears those down. Cheap (~50ms).
        if (Page.Url.StartsWith(trimmedBase, StringComparison.Ordinal))
        {
            await Page.GotoAsync("about:blank",
                new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 5_000 });
        }

        await Page.GotoAsync(trimmedBase + trimmedRel,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
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
        // BTCPay redirects to /stores/{id}/ (onboarding page for fresh
        // stores). The General settings page exposes the store id in #Id;
        // BTCPay's sidebar nav items use the convention
        // #menu-item-{StoreNavPages enum value} — see
        // BTCPayServer.Tests.PlaywrightTester.GoToStore for the reference.
        await Page.ClickAsync("#menu-item-General");
        return await Page.InputValueAsync("#Id");
    }

    /// <summary>
    /// Creates a store and sets up its Arkade wallet through the plugin's
    /// initial-setup wizard. Pass <c>null</c> to take the "Create a new wallet"
    /// (HD) path; pass any non-null string (nsec, BIP-39 seed phrase, npub,
    /// or existing wallet-id) to take the import path. Returns the storeId
    /// once the wizard has redirected away from /initial-setup.
    /// </summary>
    protected async Task<string> CreateStoreWithArkWalletAsync(string? walletInput = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/initial-setup");

        if (walletInput is null)
        {
            // Submit the "new HD wallet" form programmatically. The button
            // sits inside a Bootstrap collapse and Playwright's actionability
            // checks race the animation — programmatic .click() on the
            // submit button bypasses that without losing form behavior.
            await Page.EvaluateAsync(
                "document.querySelector('[data-testid=\"create-wallet-btn\"]').click()");
        }
        else
        {
            // The "import existing wallet" form has a required text input;
            // setting .value directly bypasses HTML5 validation in the same
            // way the user submitting the visible form does.
            await Page.EvaluateAsync(
                "(v) => { var el = document.querySelector('[data-testid=\"nsec-input\"]'); el.value = v; el.dispatchEvent(new Event('input', { bubbles: true })); }",
                walletInput);
            await Page.EvaluateAsync(
                "document.querySelector('[data-testid=\"import-wallet-btn\"]').click()");
        }

        // The InitialSetup POST redirects somewhere away from /initial-setup
        // on success. New HD wallets go through BTCPay's seed-backup screen
        // first (RecoverySeedBackup) before landing on /overview; everything
        // else (nsec / seed-phrase / npub / wallet-id) redirects straight
        // to /overview.
        //
        // Generous timeout because the first wallet creation in a session
        // involves arkd signer registration + a contract derive on a cold
        // gRPC connection (~20-30s on a fresh BTCPay process).
        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });

        if (Page.Url.Contains("/recovery-seed-backup", StringComparison.Ordinal))
        {
            // BTCPay shows the new mnemonic for safekeeping and asks the
            // user to tick a "I've written it down" box before continuing.
            // Form action posts back to the ReturnUrl we set to
            // /plugins/ark/stores/{storeId}/overview.
            await Page.CheckAsync("#confirm");
            await Page.ClickAsync("form#RecoveryConfirmation button#submit");
            await Page.WaitForURLAsync(
                url => !url.Contains("/recovery-seed-backup"),
                new PageWaitForURLOptions { Timeout = 30_000 });
        }

        // Wait for the landing page (typically /overview) to be DOM-ready so
        // the next navigation isn't queued behind an in-flight load. The
        // Arkade overview kicks off VTXO sync XHRs which can keep the page's
        // network busy indefinitely — explicitly wait for DOMContentLoaded
        // rather than the full `load` event.
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        return storeId;
    }

    /// <summary>
    /// Reads the ASP.NET antiforgery token rendered into the current page
    /// as <c>&lt;input name="__RequestVerificationToken" value="..." /&gt;</c>.
    /// BTCPay's antiforgery filter accepts it via the
    /// <c>RequestVerificationToken</c> header for AJAX requests.
    /// Returns null when no token is present (e.g., on /register before
    /// login).
    /// </summary>
    protected async Task<string?> GetAntiforgeryTokenAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);
        var locator = Page.Locator("input[name='__RequestVerificationToken']").First;
        if (await locator.CountAsync() == 0) return null;
        return await locator.GetAttributeAsync("value");
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
