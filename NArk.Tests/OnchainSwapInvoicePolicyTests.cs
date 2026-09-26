using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Models;
using Xunit;

namespace NArk.Tests;

public class OnchainSwapInvoicePolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(ArkadeSwapIntentStatus.Pending)]
    [InlineData(ArkadeSwapIntentStatus.Claimable)]
    public void AnOpenSwapKeepsItsPayoutWatchedAfterTheInvoiceExpires(ArkadeSwapIntentStatus swap)
    {
        Assert.Equal(ContractActivityState.AwaitingFundsBeforeDeactivate,
            OnchainSwapInvoicePolicy.PayoutActivity(InvoiceStatus.Expired, swap));
    }

    [Theory]
    [InlineData(ArkadeSwapIntentStatus.Fulfilled)]
    [InlineData(ArkadeSwapIntentStatus.Resolved)]
    [InlineData(ArkadeSwapIntentStatus.Cancelled)]
    public void AFinishedSwapLetsItsPayoutGo(ArkadeSwapIntentStatus swap)
    {
        Assert.Equal(ContractActivityState.Inactive,
            OnchainSwapInvoicePolicy.PayoutActivity(InvoiceStatus.Expired, swap));
    }

    [Fact]
    public void ANewInvoiceKeepsItsPayoutActive_AndAnUnknownSwapDoesNotHoldIt()
    {
        Assert.Equal(ContractActivityState.Active,
            OnchainSwapInvoicePolicy.PayoutActivity(InvoiceStatus.New, ArkadeSwapIntentStatus.Pending));
        Assert.Equal(ContractActivityState.Inactive,
            OnchainSwapInvoicePolicy.PayoutActivity(InvoiceStatus.Settled, null));
    }

    [Fact]
    public void AQuoteThatOutlivesTheCheckout_IsOffered()
    {
        Assert.True(OnchainSwapInvoicePolicy.CoversCheckout(Now.AddMinutes(15).ToUnixTimeSeconds(), Now.AddMinutes(15)));
        Assert.True(OnchainSwapInvoicePolicy.CoversCheckout(Now.AddMinutes(20).ToUnixTimeSeconds(), Now.AddMinutes(15)));
    }

    [Fact]
    public void AQuoteThatRunsOutFirst_IsNotOffered()
    {
        // A 60-minute checkout against the reference solver's 15-minute quote: boarding instead.
        Assert.False(OnchainSwapInvoicePolicy.CoversCheckout(Now.AddMinutes(15).ToUnixTimeSeconds(), Now.AddMinutes(60)));
    }

    [Fact]
    public void AFundedSwapExtendsTheInvoiceToItsClaimDeadline()
    {
        var deadline = Now.AddHours(3).ToUnixTimeSeconds();

        var extension = OnchainSwapInvoicePolicy.ExtensionFor(Now.AddMinutes(10), Now, deadline);

        Assert.Equal(TimeSpan.FromHours(3), extension);
    }

    [Fact]
    public void AnInvoiceAlreadyOpenPastTheDeadline_IsNotExtended()
    {
        var deadline = Now.AddHours(3).ToUnixTimeSeconds();

        Assert.Null(OnchainSwapInvoicePolicy.ExtensionFor(Now.AddHours(3), Now, deadline));
        Assert.Null(OnchainSwapInvoicePolicy.ExtensionFor(Now.AddMinutes(10), Now.AddHours(4), deadline));
    }

    [Fact]
    public void TheSolverFee_IsWhatTheInvoiceWasCreditedLessWhatLanded()
    {
        Assert.Equal(250, OnchainSwapInvoicePolicy.SolverFee(25_000, 24_750));
    }

    [Fact]
    public void APaymentThatIsNotASwapClaim_ReportsNoFee()
    {
        Assert.Null(OnchainSwapInvoicePolicy.SolverFee(null, 24_750));
        Assert.Null(OnchainSwapInvoicePolicy.SolverFee(25_000, 25_000));
    }

    [Fact]
    public void AClaimLargerThanTheCredit_ReportsNoFee()
    {
        Assert.Null(OnchainSwapInvoicePolicy.SolverFee(25_000, 25_100));
    }
}
