using BTCPayServer.Client.Models;
using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>How an invoice paid through the onchain swap outlives its checkout window.</summary>
public static class OnchainSwapInvoicePolicy
{
    // The claim pays the prompt's own contract and can land hours after the invoice stops being New,
    // so while the swap is open that contract stays watched and switches itself off once paid.
    public static ContractActivityState PayoutActivity(InvoiceStatus invoice, ArkadeSwapIntentStatus? swap) =>
        invoice == InvoiceStatus.New ? ContractActivityState.Active
        : swap is { } s && !ArkadeSwapStateMachine.Terminal.Contains(s) ? ContractActivityState.AwaitingFundsBeforeDeactivate
        : ContractActivityState.Inactive;

    // A quote the payer can no longer fund is an address the checkout must stop showing: the solver drops
    // an unfunded quote of its own accord, and money sent after that waits out the HTLC locktime instead
    // of paying anything. Boarding has no such deadline.
    public static bool CoversCheckout(long quoteValidUntil, DateTimeOffset invoiceExpiry) =>
        quoteValidUntil >= invoiceExpiry.ToUnixTimeSeconds();

    // BTCPay has no field for a fee the merchant bears — PaymentMethodFee is added to what the payer
    // owes, and booking one here would leave the invoice underpaid — so this is for display only.
    public static long? SolverFee(long? creditedGrossSats, long landedSats) =>
        creditedGrossSats is { } gross && gross > landedSats ? gross - landedSats : null;

    // A funded HTLC keeps the invoice open until the swap's claim deadline, the last moment it can be paid.
    public static TimeSpan? ExtensionFor(DateTimeOffset invoiceExpiry, DateTimeOffset now, long claimDeadline)
    {
        var target = DateTimeOffset.FromUnixTimeSeconds(claimDeadline);
        return target > invoiceExpiry && target > now ? target - now : null;
    }
}
