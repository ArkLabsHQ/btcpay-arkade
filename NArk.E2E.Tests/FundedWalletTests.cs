using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Tests that need a wallet with spendable VTXOs. Funding mints an arkd
/// credit note and imports it through the plugin's in-process
/// <c>IContractService</c> — the same path NNark's NoteTests uses. arkd's
/// indexer then reports the note as a VTXO; the plugin's
/// IntentGenerationService (shortened to a 5s poll for the suite via
/// BTCPAY_ARKINTENTPOLLSECONDS) generates the redemption intent, and the
/// next batch turns it into a real spendable VTXO.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class FundedWalletTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public FundedWalletTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Fund a fresh nsec store by minting a note and importing it as an
    /// <see cref="ArkNoteContract"/>; assert the overview balance reflects
    /// the redeemed VTXO.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FundViaNoteImport_VtxoArrives()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrEmpty(walletId), "store has no wallet id");

        const long fundingSats = 50_000;
        await FundWalletViaNoteAsync(walletId!, fundingSats);

        // arkd charges a 1% offchain-input intent fee when redeeming the
        // note (see /v1/info fees.intentFee.offchainInput = amount*0.01),
        // so a 50k note nets ~49.5k. Wait for "most of it" to arrive.
        var minExpected = (long)(fundingSats * 0.97);
        var balance = await PollForBalanceAsync(storeId, minExpected);
        Assert.True(balance >= minExpected,
            $"balance {balance} never reached {minExpected} (note was {fundingSats}, ~1% fee expected)");
    }

    /// <summary>
    /// With a funded wallet, /estimate-fees for an Ark→Ark transfer should
    /// return a fee field (regtest fee may be 0, so assert the response
    /// shape, not a positive value).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EstimateFees_FundedWallet_ReturnsFeeField()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundWalletViaNoteAsync(walletId!, 200_000);
        // ~1% arkd intent fee on note redemption; wait for the net amount.
        await PollForBalanceAsync(storeId, (long)(200_000 * 0.97));

        // Recipient store just to harvest a valid Ark address.
        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/estimate-fees").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destination = recipientAddr, amountSats = 50_000L }
            });

        if (!resp.Ok)
        {
            var body = await resp.TextAsync();
            throw new InvalidOperationException($"estimate-fees returned {resp.Status}: {body}");
        }
        var json = await resp.JsonAsync();
        Assert.NotNull(json);
        var hasFee = json!.Value.TryGetProperty("feeSats", out _) ||
                     json.Value.TryGetProperty("totalFeeSats", out _) ||
                     json.Value.TryGetProperty("intentFeeSats", out _) ||
                     json.Value.TryGetProperty("estimatedFeeSats", out _);
        Assert.True(hasFee, $"estimate-fees response missing a fee field: {json}");
    }

    private Task FundWalletViaNoteAsync(string walletId, long amountSats) =>
        FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId, amountSats);
}
