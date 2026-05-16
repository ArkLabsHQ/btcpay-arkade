using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Exercises BTCPay payouts via the ARKADE payout method. Pull-payment
/// and payout creation go through Greenfield (basic auth as the
/// registered admin) — not the invoice payment-prompt path, so they're
/// unaffected by the invoice-creation hang.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class PayoutTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public PayoutTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A store with an Arkade wallet should expose ARKADE as a usable
    /// pull-payment payout method, and creating one should succeed.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreatePullPayment_WithArkadeMethod_Succeeds()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Arkade payout test",
            Amount = 0.001m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });

        Assert.False(string.IsNullOrEmpty(pp.Id));
    }

    /// <summary>
    /// Claiming a payout against the pull payment with an Arkade address
    /// destination should land in AwaitingApproval (no funds move yet).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClaimPayout_ToArkAddress_AwaitingApproval()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        // Harvest a real Arkade address from a second store.
        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Claim test",
            Amount = 0.001m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });

        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = recipientAddr,
            Amount = 0.0005m,
            PayoutMethodId = "ARKADE"
        });

        Assert.False(string.IsNullOrEmpty(payout.Id));
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);
    }

    /// <summary>
    /// Full flow: fund the store wallet, create a pull payment, claim a
    /// payout to an Arkade address, approve it, and assert the
    /// ArkAutomatedPayoutSender advances it past AwaitingApproval
    /// (AwaitingPayment / InProgress / Completed all count as "the sender
    /// picked it up").
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApprovePayout_FundedWallet_SenderAdvancesState()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundWalletViaNoteAsync(walletId!, 200_000);
        await PollForBalanceAsync(storeId, (long)(200_000 * 0.97));

        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Approve test",
            Amount = 0.001m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });
        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = recipientAddr,
            Amount = 0.0005m,
            PayoutMethodId = "ARKADE"
        });
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);

        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

        // The automated Ark payout sender should move it off
        // AwaitingApproval within a couple of batch cycles.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        PayoutState lastState = PayoutState.AwaitingApproval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var p = await client.GetStorePayout(storeId, payout.Id);
            lastState = p.State;
            if (lastState is PayoutState.AwaitingPayment
                or PayoutState.InProgress
                or PayoutState.Completed)
                return;
            if (lastState == PayoutState.Cancelled)
                Assert.Fail("payout was cancelled instead of being processed");
            await Task.Delay(3_000);
        }
        Assert.Fail($"payout never advanced past AwaitingApproval (last: {lastState})");
    }

    private Task FundWalletViaNoteAsync(string walletId, long amountSats) =>
        FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId, amountSats);
}
