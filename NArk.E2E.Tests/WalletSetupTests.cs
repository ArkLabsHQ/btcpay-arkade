using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

[Collection("Arkade Plugin Tests")]
public class WalletSetupTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public WalletSetupTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Smoke test: register an admin, create a store, navigate to the
    /// plugin's initial-setup page, and confirm both wallet-creation
    /// options are rendered. Validates that the plugin DLL loaded and
    /// the controller is wired up — no Ark-side state is exercised.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegisterAndCreateStore_NavigateToArkWallet_ShowsSetupPage()
    {
        _fixture.Initialize(this);
        var server = _fixture.ServerTester!;

        await InitializePlaywright(server);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStore();

        // Plugin controller routes are mounted under /plugins/ark/...
        // The overview action redirects to initial-setup when no wallet
        // is configured yet, so this single URL works for fresh stores.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        var hdOption = Page.Locator("[data-testid='hd-wallet-option']");
        var legacyOption = Page.Locator("[data-testid='legacy-wallet-option']");

        Assert.Equal(1, await hdOption.CountAsync());
        Assert.Equal(1, await legacyOption.CountAsync());
    }
}
