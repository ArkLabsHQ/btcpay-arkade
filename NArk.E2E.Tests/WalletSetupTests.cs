using Microsoft.Playwright;
using NArk.Tests.End2End.Common;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
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

    /// <summary>
    /// Generate a valid bech32-encoded nsec (Nostr private key) using a
    /// fresh random ECPrivKey. Mirrors WalletFactory.DecodeNsecPrivKey's
    /// inverse: SquashBytes + StrictLength=false.
    /// </summary>
    private static string GenerateRandomNsec()
    {
        Span<byte> keyBytes = stackalloc byte[32];
        Random.Shared.NextBytes(keyBytes);
        // Ensure the bytes form a valid secp256k1 scalar
        if (!ECPrivKey.TryCreate(keyBytes, out _))
        {
            // Vanishingly unlikely; just use a known-valid scalar
            keyBytes.Clear();
            keyBytes[31] = 0x01;
        }

        var encoder = Encoders.Bech32("nsec");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(keyBytes.ToArray(), Bech32EncodingType.BECH32);
    }
}
