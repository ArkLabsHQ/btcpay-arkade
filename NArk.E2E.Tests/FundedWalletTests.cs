using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// One consolidated funded-wallet journey. Funding mints arkd credit
/// notes and imports them via the plugin's in-process IContractService;
/// arkd's indexer reports them as VTXOs and the IntentGenerationService
/// (5s poll for the suite via BTCPAY_ARKINTENTPOLLSECONDS) redeems them
/// into spendable VTXOs.
///
/// This is deliberately ONE test rather than four. Each independent
/// note→redemption cycle contends for arkd's ~40s batch window; running
/// four of them back-to-back on shared CI infra is flaky. Funding once
/// (two notes, redeemed together) and asserting estimate-fees / send /
/// payout sequentially against that one wallet removes the contention.
/// Trade-off: coarser failure isolation, accepted for CI stability.
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FundedWallet_FullJourney_FundEstimateSendPayout()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrEmpty(walletId), "store has no wallet id");

        // Fund with two notes so a single redemption batch yields two
        // independent VTXOs — the send and the payout each take one
        // without an inter-step change-settle wait.
        await FundWalletViaNoteAsync(walletId!, 250_000);
        await FundWalletViaNoteAsync(walletId!, 250_000);

        // Wait on the real readiness signal — spendable coins, not the
        // rendered balance (which counts a note VTXO before it's
        // redeemed/spendable and yields a false positive).
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "ArkAddress", 40_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(outpoints);

        // Recipient store — just to harvest a valid Arkade address.
        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");
        Assert.False(string.IsNullOrWhiteSpace(recipientAddr));

        // 1) estimate-fees for an Ark→Ark transfer returns a fee field.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var feeResp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/estimate-fees").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destination = recipientAddr, amountSats = 40_000L }
            });
        if (!feeResp.Ok)
            throw new InvalidOperationException(
                $"estimate-fees returned {feeResp.Status}: {await feeResp.TextAsync()}");
        var feeJson = await feeResp.JsonAsync();
        var hasFee = feeJson!.Value.TryGetProperty("feeSats", out _) ||
                     feeJson.Value.TryGetProperty("totalFeeSats", out _) ||
                     feeJson.Value.TryGetProperty("intentFeeSats", out _) ||
                     feeJson.Value.TryGetProperty("estimatedFeeSats", out _);
        Assert.True(hasFee, $"estimate-fees response missing a fee field: {feeJson}");

        // 2) Send 40k to the recipient via build-intent (uses one VTXO).
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        token = (await GetAntiforgeryTokenAsync()) ?? "";
        var sendResp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(recipientAddr)}" +
                       $"&Outputs[0].AmountBtc={(40_000 / 100_000_000m).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            });
        Assert.True(sendResp.Ok, $"build-intent returned {sendResp.Status}");
        var sendBody = await sendResp.TextAsync();
        Assert.DoesNotContain("No valid VTXOs selected", sendBody);

        // 3) Payout: create a pull payment, claim to the recipient,
        //    approve it, and assert the ArkAutomatedPayoutSender advances
        //    it off AwaitingApproval. Uses the second funded VTXO.
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Journey payout",
            Amount = 0.002m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });
        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = recipientAddr,
            Amount = 0.0015m,
            PayoutMethodId = "ARKADE"
        });
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);
        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        PayoutState last = PayoutState.AwaitingApproval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var p = await client.GetStorePayout(storeId, payout.Id);
            last = p.State;
            if (last is PayoutState.AwaitingPayment or PayoutState.InProgress or PayoutState.Completed)
                return;
            if (last == PayoutState.Cancelled)
                Assert.Fail("payout was cancelled instead of processed");
            await Task.Delay(3_000);
        }
        Assert.Fail($"payout never advanced past AwaitingApproval (last: {last})");
    }

    private Task FundWalletViaNoteAsync(string walletId, long amountSats) =>
        FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId, amountSats);
}
