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

    // The funded approve→ArkAutomatedPayoutSender flow lives in the
    // consolidated FundedWalletTests journey (one funding cycle for all
    // funded assertions — see that file for the rationale).
}
