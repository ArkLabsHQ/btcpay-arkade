using Microsoft.Playwright;
using NBitcoin;
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
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var hdOption = Page.Locator("[data-testid='hd-wallet-option']");
        var legacyOption = Page.Locator("[data-testid='legacy-wallet-option']");

        Assert.Equal(1, await hdOption.CountAsync());
        Assert.Equal(1, await legacyOption.CountAsync());
    }

    /// <summary>
    /// Take the "Create a new wallet" path of the wizard. After submission
    /// the controller should generate a fresh BIP-39 HD wallet, persist it,
    /// and redirect away from /initial-setup (typically to /overview).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateNewHotWallet_LandsOnOverview()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync();

        Assert.Contains($"/plugins/ark/stores/{storeId}", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
    }

    /// <summary>
    /// Import the legacy nsec (Nostr private key) path — the controller
    /// should create a SingleKey wallet and redirect to overview.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportNsec_StoresWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var nsec = GenerateRandomNsec();
        var storeId = await CreateStoreWithArkWalletAsync(nsec);

        Assert.Contains($"/plugins/ark/stores/{storeId}", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
    }

    /// <summary>
    /// Import a 12-word BIP-39 mnemonic — the controller should create an
    /// HD wallet and redirect to overview.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportBip39SeedPhrase_StoresHdWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var storeId = await CreateStoreWithArkWalletAsync(mnemonic);

        Assert.Contains($"/plugins/ark/stores/{storeId}", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
    }

    /// <summary>
    /// Garbage input fails parsing; the wizard re-renders with a
    /// validation error and stays on /initial-setup. (Plugin error
    /// surfacing route: TempData[WellKnownTempData.ErrorMessage].)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvalidWalletInput_ShowsValidationError()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/initial-setup");

        await Page!.ClickAsync("[data-testid='legacy-wallet-option']");
        await Page.FillAsync("[data-testid='nsec-input']", "not-a-valid-wallet-format-xyzzy");
        await Page.ClickAsync("[data-testid='import-wallet-btn']");

        // Wait briefly for the form post to round-trip and re-render
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains("/initial-setup", Page.Url);
        // The error message ends up in a BTCPay alert; assert that
        // SOMETHING about "Unsupported value" or "Could not update wallet"
        // surfaces. (Controller throws → TempData error message.)
        var bodyText = await Page.InnerTextAsync("body");
        var sawError = bodyText.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ||
                       bodyText.Contains("Could not update wallet", StringComparison.OrdinalIgnoreCase);
        Assert.True(sawError, $"Expected an error message but page body was:\n{bodyText[..Math.Min(500, bodyText.Length)]}");
    }

    /// <summary>
    /// Pass another store's wallet receive address as the import value.
    /// The controller treats this as the destination for a transitory
    /// auto-sweep wallet (npub path) and creates a fresh local wallet
    /// that sweeps to the donor address.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportNpub_CreatesTransitoryWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        // Donor store: nsec (SingleKey) wallet so the overview exposes a
        // deterministic Arkade address. HD wallets don't render a default
        // address on /overview — they derive per-receive instead.
        var donorStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{donorStoreId}/overview");
        var donorAddress = await Page!.InputValueAsync("[data-testid='receive-address']");
        Assert.False(string.IsNullOrWhiteSpace(donorAddress), "donor store has no receive address");

        // New store: import the donor address as the transitory destination.
        var transitoryStoreId = await CreateStoreWithArkWalletAsync(donorAddress);

        Assert.NotEqual(donorStoreId, transitoryStoreId);
        Assert.DoesNotContain("/initial-setup", Page.Url);
    }

    /// <summary>
    /// Import an existing wallet by id. A wallet is created on store A;
    /// its WalletId is scraped from the overview page and pasted into
    /// store B's wizard. Both stores should reference the same wallet.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportWalletId_ReusesExistingWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeAId = await CreateStoreWithArkWalletAsync();
        await GoToUrl($"/plugins/ark/stores/{storeAId}/overview");
        var walletId = await Page!.GetAttributeAsync(".truncate-center-id", "data-text");
        Assert.False(string.IsNullOrWhiteSpace(walletId), "store A has no wallet id");

        var storeBId = await CreateStoreWithArkWalletAsync(walletId);
        Assert.NotEqual(storeAId, storeBId);
        Assert.DoesNotContain("/initial-setup", Page.Url);

        // Verify store B's overview shows the same wallet id.
        await GoToUrl($"/plugins/ark/stores/{storeBId}/overview");
        var storeBWalletId = await Page.GetAttributeAsync(".truncate-center-id", "data-text");
        Assert.Equal(walletId, storeBWalletId);
    }

    /// <summary>
    /// Download the per-wallet diagnostic log file. The endpoint returns
    /// 200 + a file body even when no log lines have been written yet.
    /// (Regression target for PR #46 — added the wallet-log feature.)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WalletLogDownload_ReturnsFile()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync();

        var resp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/wallet-log").AbsoluteUri);
        Assert.True(resp.Ok, $"wallet-log endpoint returned {resp.Status}");
    }

}
