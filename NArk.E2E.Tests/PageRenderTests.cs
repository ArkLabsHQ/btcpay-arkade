using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Verifies the plugin's user-facing pages return 200 OK for a logged-in
/// store admin. Uses Page.Context.APIRequest.GetAsync rather than Playwright
/// navigation so the long-polling overview page doesn't block subsequent
/// requests on Chromium's per-origin connection pool.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class PageRenderTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public PageRenderTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("overview")]
    [InlineData("receive")]
    [InlineData("send")]
    [InlineData("spend")]
    [InlineData("contracts")]
    [InlineData("swaps")]
    public async Task PluginPage_Returns200(string subpath)
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        var resp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/{subpath}").AbsoluteUri);

        Assert.True(resp.Ok, $"/plugins/ark/stores/.../{subpath} returned {resp.Status}");
        var html = await resp.TextAsync();
        // Sanity check: pages should at minimum have the BTCPay layout
        // (header with the store dropdown). If they returned a 200 but
        // rendered an error page, this catches it.
        Assert.Contains("BTCPay", html);
    }

    /// <summary>
    /// The initial-setup endpoint should redirect (302) to /overview once
    /// the wallet is configured — the wizard is for first-time setup only.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InitialSetup_RedirectsWhenWalletAlreadyConfigured()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        // MaxRedirects = 0 surfaces the raw 302; the default follows it
        // and the test would assert on the wrong URL.
        var resp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/initial-setup").AbsoluteUri,
            new APIRequestContextOptions { MaxRedirects = 0 });

        Assert.Equal(302, resp.Status);
        var location = resp.Headers["location"];
        Assert.NotNull(location);
        Assert.Contains("/overview", location);
    }

    /// <summary>
    /// Plugin endpoints under /plugins/ark/* require store-modify permission
    /// (cookie auth). Unauthenticated requests should redirect to /login.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PluginPage_RedirectsUnauthenticatedToLogin()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        // Brand new browser context — no session cookie.
        var ctx = await Browser!.NewContextAsync();
        var resp = await ctx.APIRequest.GetAsync(
            new Uri(ServerUri!, "/plugins/ark/stores/some-fake-id/overview").AbsoluteUri,
            new APIRequestContextOptions { MaxRedirects = 0 });

        // BTCPay returns either 302 (redirect to /Account/Login) or 401
        // (Unauthorized) depending on the route's auth scheme. Both are
        // acceptable evidence that the plugin enforces auth — only 200
        // would be a bug.
        Assert.True(
            resp.Status is 302 or 401 or 403,
            $"Expected auth challenge (302/401/403), got {resp.Status}");
    }

}
