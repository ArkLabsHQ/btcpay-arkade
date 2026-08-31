using BTCPayServer.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Turns an Arkade swap intent into the invoice or payment BTCPay reasons about.
/// </summary>
/// <remarks>
/// The whole of this corridor's meaning for BTCPay is in these two mappings, because BTCPay decides
/// whether an order is settled from the status alone. The rule below is deliberately conservative in
/// one direction and not the other: an invoice counts as paid only once the swap is
/// <see cref="ArkadeSwapIntentStatus.Fulfilled"/>, never merely funded. Calling a funded-but-unclaimed
/// swap paid would mark an order settled while the money still sits in a script we have not spent,
/// on a clock, with the counterparty's own reclaim path opening at the end of it.
/// </remarks>
public static class ArkadeIntentLightningMapper
{
    /// <summary>Map a receive swap to the invoice BTCPay handed out.</summary>
    /// <param name="intent">The swap, which must be a Lightning receive.</param>
    /// <param name="network">The network its invoice was minted on.</param>
    /// <returns>The invoice, or <c>null</c> when the swap carries none.</returns>
    public static LightningInvoice? ToInvoice(ArkadeSwapIntent intent, Network network)
    {
        if (intent.Invoice is not { Length: > 0 } bolt11)
        {
            return null;
        }

        var decoded = BOLT11PaymentRequest.Parse(bolt11, network);
        var status = intent.Status switch
        {
            ArkadeSwapIntentStatus.Fulfilled => LightningInvoiceStatus.Paid,

            // The solver's reclaim window is open or the swap ended without us taking delivery.
            ArkadeSwapIntentStatus.Cancelled
                or ArkadeSwapIntentStatus.Recoverable
                or ArkadeSwapIntentStatus.Refundable
                or ArkadeSwapIntentStatus.Resolved => LightningInvoiceStatus.Expired,

            // Funding, Pending, Claimable, Cancelling. Claimable is the interesting one: the solver
            // has funded and the money is ours to take, but until the claim lands nothing has been
            // received — and the payer's own invoice is a hold that only our claim settles.
            _ => LightningInvoiceStatus.Unpaid,
        };

        return new LightningInvoice
        {
            Id = intent.Id,
            Amount = decoded.MinimumAmount,
            Status = status,
            ExpiresAt = decoded.ExpiryDate,
            BOLT11 = bolt11,
            PaymentHash = decoded.PaymentHash?.ToString(),
            PaidAt = status == LightningInvoiceStatus.Paid ? intent.CreatedAt.ToUniversalTime() : null,
            Preimage = intent.Preimage,
        };
    }

    /// <summary>Map a send swap to the payment BTCPay is waiting on.</summary>
    /// <param name="intent">The swap, which must be a Lightning send.</param>
    /// <param name="network">The network its invoice belongs to.</param>
    /// <returns>The payment.</returns>
    public static LightningPayment ToPayment(ArkadeSwapIntent intent, Network network)
    {
        var status = intent.Status switch
        {
            ArkadeSwapIntentStatus.Fulfilled => LightningPaymentStatus.Complete,

            // Refundable means the locktime passed with nobody filling: the payment did not happen and
            // the deposit is ours to take back. Resolved is the ambiguous end — spent past the
            // locktime, by a fill or by a refund, and only the witness says which. Neither is a
            // payment BTCPay should keep waiting on.
            ArkadeSwapIntentStatus.Refundable
                or ArkadeSwapIntentStatus.Cancelled
                or ArkadeSwapIntentStatus.Recoverable
                or ArkadeSwapIntentStatus.Resolved => LightningPaymentStatus.Failed,

            _ => LightningPaymentStatus.Pending,
        };

        var amount = LightMoney.Satoshis(intent.WantAmount.Satoshi);
        var locked = LightMoney.Satoshis(intent.OfferAmount.Satoshi);

        return new LightningPayment
        {
            Id = intent.Id,
            PaymentHash = intent.PaymentHash,
            Status = status,
            BOLT11 = intent.Invoice,
            Preimage = intent.Preimage,
            CreatedAt = intent.CreatedAt,
            Amount = amount,
            AmountSent = locked,
            // The corridor's fee is the spread, and there is no separate field to read it from.
            Fee = locked - amount,
        };
    }
}
