using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
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

    private static string? _resolvedArkdContainer;

    /// <summary>
    /// Resolves the arkd container name. nigiri's own lib/env.sh names it
    /// "arkd" when a custom ARKD_IMAGE is supplied (typical local dev) and
    /// "ark" for the built-in image (CI). Probe both with `docker inspect`
    /// and cache the first that exists rather than hardcoding either.
    /// </summary>
    protected static async Task<string> ResolveArkdContainerAsync()
    {
        if (_resolvedArkdContainer is not null) return _resolvedArkdContainer;
        foreach (var candidate in new[] { "arkd", "ark" })
        {
            var probe = await CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(
                CliWrap.Cli.Wrap("docker")
                    .WithArguments(new[] { "inspect", "-f", "{{.Name}}", candidate })
                    .WithValidation(CliWrap.CommandResultValidation.None));
            if (probe.IsSuccess)
                return _resolvedArkdContainer = candidate;
        }
        throw new InvalidOperationException(
            "Could not find an arkd container (tried 'arkd' and 'ark'). Is the nigiri stack up?");
    }

    /// <summary>
    /// Mint a credit note via the arkd admin CLI. The container name is
    /// resolved dynamically (see <see cref="ResolveArkdContainerAsync"/>);
    /// the in-container binary is always <c>arkd</c> regardless of name.
    /// </summary>
    protected static async Task<string> CreateArkNoteAsync(long amountSats)
    {
        var container = await ResolveArkdContainerAsync();
        var result = await CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(
            CliWrap.Cli.Wrap("docker")
                .WithArguments(new[]
                {
                    "exec", container, "arkd", "note", "--amount", amountSats.ToString()
                })
                .WithValidation(CliWrap.CommandResultValidation.None));

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"arkd note --amount {amountSats} (container '{container}') failed " +
                $"(exit={result.ExitCode}): stdout={result.StandardOutput.Trim()}, " +
                $"stderr={result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
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

    /// <summary>
    /// Generates a valid bech32-encoded nsec (Nostr private key) from a
    /// fresh random secp256k1 scalar. Importing this through the wizard
    /// yields a SingleKey wallet with a deterministic Arkade address that
    /// the overview page renders.
    /// </summary>
    protected static string GenerateRandomNsec()
    {
        Span<byte> keyBytes = stackalloc byte[32];
        Random.Shared.NextBytes(keyBytes);
        if (!ECPrivKey.TryCreate(keyBytes, out _))
        {
            keyBytes.Clear();
            keyBytes[31] = 0x01;
        }
        var encoder = Encoders.Bech32("nsec");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(keyBytes.ToArray(), Bech32EncodingType.BECH32);
    }

    /// <summary>
    /// Resolves the BTCPay store's configured Arkade walletId by scraping
    /// the truncate-center element on the overview page.
    /// </summary>
    protected async Task<string?> GetStoreWalletIdAsync(string storeId)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        return await Page.GetAttributeAsync(".truncate-center-id", "data-text");
    }

    /// <summary>
    /// Funds a wallet by minting an arkd credit note and importing it as
    /// an <see cref="ArkNoteContract"/> through the plugin's in-process
    /// <see cref="IContractService"/> (resolved from the running BTCPay's
    /// service provider). arkd's indexer then reports the note as a VTXO;
    /// the redemption intent is generated on the suite's shortened poll.
    /// </summary>
    protected async Task FundWalletViaNoteAsync(
        IServiceProvider serviceProvider, string walletId, long amountSats)
    {
        var note = await CreateArkNoteAsync(amountSats);
        if (string.IsNullOrEmpty(note))
            throw new InvalidOperationException("arkd note CLI returned empty");
        var contractService = serviceProvider.GetRequiredService<IContractService>();
        await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
    }

    /// <summary>
    /// Reads [data-testid='wallet-balance'] from the current overview
    /// page. The plugin renders available balance as a BTC-denominated
    /// string (DisplayFormatter.Currency); the first decimal in the text
    /// is parsed and ×1e8 to sats.
    /// </summary>
    protected async Task<long> ReadAvailableBalanceSatsAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);
        var locator = Page.Locator("[data-testid='wallet-balance']").First;
        if (await locator.CountAsync() == 0) return 0;
        var text = await locator.InnerTextAsync();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:[.,]\d+)?");
        if (!match.Success) return 0;
        if (!decimal.TryParse(
                match.Value.Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var btc)) return 0;
        return (long)(btc * 100_000_000m);
    }

    /// <summary>
    /// Polls the overview balance until it reaches <paramref name="minSats"/>
    /// or the timeout elapses. Returns the last observed balance on success.
    /// </summary>
    protected async Task<long> PollForBalanceAsync(
        string storeId, long minSats, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        long last = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
            last = await ReadAvailableBalanceSatsAsync();
            if (last >= minSats) return last;
            await Task.Delay(3_000);
        }
        throw new TimeoutException(
            $"Wallet {storeId} balance never reached {minSats} sats (last: {last}).");
    }

    /// <summary>
    /// Polls /suggest-coins until it returns spendable outpoints (the real
    /// precondition for spend/payout/estimate), not just a rendered balance.
    /// The Arkade overview shows an inbound note VTXO the instant arkd's
    /// indexer reports it, but it isn't spendable until the redemption batch
    /// settles — waiting on the displayed balance races that gap and yields
    /// "No spendable coins" the moment you try to use it. Returns the
    /// outpoints once available.
    /// </summary>
    protected async Task<List<string>> PollForSpendableCoinsAsync(
        string storeId, string destinationType, long amountSats, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var outpoints = await SuggestOutpointsAsync(storeId, destinationType, amountSats);
                if (outpoints.Count > 0) return outpoints;
            }
            // Transient "coins not ready yet" responses: a just-redeemed
            // note VTXO is briefly absent ("No spendable coins") and, for
            // the Lightning path specifically, briefly classified
            // recoverable/swept ("No non-recoverable coins available")
            // until it settles. Both clear on the next batch — keep polling.
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("No spendable coins") ||
                ex.Message.Contains("non-recoverable coins"))
            {
                lastError = ex.Message;
            }
            await Task.Delay(3_000);
        }
        throw new TimeoutException(
            $"Store {storeId} had no spendable coins within the wait window (last: {lastError ?? "empty selection"}).");
    }

    /// <summary>
    /// Calls POST /suggest-coins for the given destination type and amount
    /// and returns the server-picked outpoints (txid:vout strings). Throws
    /// if the endpoint reports an error (e.g. no spendable coins).
    /// </summary>
    protected async Task<List<string>> SuggestOutpointsAsync(
        string storeId, string destinationType, long amountSats)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/suggest-coins").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destinationType, amountSats }
            });

        var raw = await resp.TextAsync();
        if (!resp.Ok)
            throw new InvalidOperationException($"suggest-coins returned {resp.Status}: {raw}");
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.GetString() is { Length: > 0 } msg)
            throw new InvalidOperationException($"suggest-coins error: {msg}");
        if (!root.TryGetProperty("suggestedOutpoints", out var op) ||
            op.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        return op.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
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
