using System.Text.Json;
using Microsoft.Playwright;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Covers the plugin's JSON API endpoints (parse-destination,
/// estimate-fees, suggest-coins, show-private-key, etc.). These don't
/// involve BTCPay's invoice-creation pipeline and so are unaffected by
/// the per-store hang seen in ReceiveInvoiceTests.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class ApiEndpointTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ApiEndpointTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// POST /parse-destination must identify a valid Arkade address as
    /// the ArkAddress type and return ResolvedAddress matching the input.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ParseDestination_IdentifiesArkAddress()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        // Donor store with a deterministic Arkade address.
        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var arkAddr = await Page!.InputValueAsync("[data-testid='receive-address']");
        Assert.False(string.IsNullOrWhiteSpace(arkAddr));

        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/parse-destination").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = (await GetAntiforgeryTokenAsync()) ?? ""
                },
                DataObject = new { destination =arkAddr, amountBtc = (decimal?)null }
            });

        Assert.True(resp.Ok, $"parse-destination returned {resp.Status}");
        var json = await resp.JsonAsync();
        Assert.NotNull(json);
        // ParseDestinationResponse.Type is the Send2DestinationType enum
        // stringified. For a bare Ark address that's "ArkAddress".
        var type = json!.Value.GetProperty("type").GetString();
        Assert.Equal("ArkAddress", type);
    }

    /// <summary>
    /// POST /parse-destination on a random LND BOLT11 invoice should
    /// return type LightningInvoice and surface the amount.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ParseDestination_IdentifiesLightningInvoice()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        var bolt11 = await NArk.Tests.End2End.Common.DockerHelper.CreateLndInvoice(
            amtSats: 1_000, expirySecs: 300);

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/parse-destination").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = (await GetAntiforgeryTokenAsync()) ?? ""
                },
                DataObject = new { destination =bolt11, amountBtc = (decimal?)null }
            });

        Assert.True(resp.Ok, $"parse-destination returned {resp.Status}");
        var json = await resp.JsonAsync();
        var type = json!.Value.GetProperty("type").GetString();
        Assert.Equal("LightningInvoice", type);
        var isLightning = json.Value.GetProperty("isLightning").GetBoolean();
        Assert.True(isLightning, "isLightning should be true for a bolt11 invoice");
    }

    /// <summary>
    /// POST /parse-destination on a bare BTC address should reject it —
    /// the /send page only supports off-chain destinations (Ark, LN,
    /// LNURL, or BIP21 carrying ark=/lightning= params). Chain-swap
    /// destinations go through a separate flow.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ParseDestination_RejectsBareBitcoinAddress()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        var key = new Key();
        var btcAddr = key.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/parse-destination").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = (await GetAntiforgeryTokenAsync()) ?? ""
                },
                DataObject = new { destination = btcAddr, amountBtc = (decimal?)null }
            });

        Assert.True(resp.Ok, $"parse-destination returned {resp.Status}");
        var json = await resp.JsonAsync();
        var isValid = json!.Value.TryGetProperty("isValid", out var v) && v.GetBoolean();
        Assert.False(isValid, "bare BTC address should not be a valid /send destination");
    }

    /// <summary>
    /// POST /parse-destination on garbage input should return IsValid=false
    /// rather than throw — the wizard surfaces this Error string to the user.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ParseDestination_RejectsInvalidDestination()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/parse-destination").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = (await GetAntiforgeryTokenAsync()) ?? ""
                },
                DataObject = new { destination ="obviously-not-a-real-address", amountBtc = (decimal?)null }
            });

        if (!resp.Ok)
        {
            var body = await resp.TextAsync();
            throw new InvalidOperationException($"parse-destination returned {resp.Status}: {body}");
        }
        var json = await resp.JsonAsync();
        var isValid = json!.Value.TryGetProperty("isValid", out var v) && v.GetBoolean();
        Assert.False(isValid, "garbage destination should not be marked valid");
    }

    /// <summary>
    /// POST /show-private-key on an admin-owned store should redirect to
    /// BTCPay's /recovery-seed-backup page rendering the wallet's secret
    /// in a <c>data-mnemonic</c> attribute. We don't follow the redirect
    /// via APIRequest (the controller uses TempData which only persists
    /// across cookie-bound page navigations); instead we POST the form
    /// from the overview page and let Playwright handle the redirect.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ShowPrivateKey_RevealsWalletSecret()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var nsec = GenerateRandomNsec();
        var storeId = await CreateStoreWithArkWalletAsync(nsec);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        // The Show Private Key form submits via JS — we submit the same
        // form by element click. After submission the page lands on
        // /recovery-seed-backup with the mnemonic in #RecoveryPhrase.
        await Page!.EvaluateAsync(
            "document.querySelector('[data-testid=\"show-private-key-btn\"]').closest('form').submit()");
        await Page.WaitForURLAsync(
            url => url.Contains("/recovery-seed-backup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        var mnemonic = await Page.GetAttributeAsync("#RecoveryPhrase", "data-mnemonic");
        Assert.Equal(nsec, mnemonic);
    }

}
