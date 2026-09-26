using BTCPayServer.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

// Paid only once Fulfilled, never merely funded: a funded-but-unclaimed swap is still on a clock with the
// solver's reclaim path opening at the end of it.
public static class ArkadeIntentLightningMapper
{
    public static LightningInvoice? ToInvoice(ArkadeSwapIntent intent, Network network)
    {
        var lightning = intent.LightningMetadata();
        if (lightning.Invoice is not { Length: > 0 } bolt11)
        {
            return null;
        }

        var decoded = BOLT11PaymentRequest.Parse(bolt11, network);
        var status = intent.Status switch
        {
            ArkadeSwapIntentStatus.Fulfilled => LightningInvoiceStatus.Paid,

            ArkadeSwapIntentStatus.Cancelled
                or ArkadeSwapIntentStatus.Recoverable
                or ArkadeSwapIntentStatus.Refundable
                or ArkadeSwapIntentStatus.Resolved => LightningInvoiceStatus.Expired,

            // Claimable included: nothing is received until our claim lands, which also settles the payer's hold.
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
            Preimage = lightning.Preimage,
        };
    }

    public static LightningPayment ToPayment(ArkadeSwapIntent intent, Network network)
    {
        var status = intent.Status switch
        {
            ArkadeSwapIntentStatus.Fulfilled => LightningPaymentStatus.Complete,

            // Failed only once our refund has landed. BTCPay cancels a failed payout and it gets paid
            // again, so Refundable (the solver can still claim), Recoverable and an unproven Resolved
            // stay pending instead.
            ArkadeSwapIntentStatus.Cancelled => LightningPaymentStatus.Failed,

            _ => LightningPaymentStatus.Pending,
        };

        var amount = LightMoney.Satoshis(intent.WantAmount.Satoshi);
        var locked = LightMoney.Satoshis(intent.OfferAmount.Satoshi);

        return new LightningPayment
        {
            Id = intent.Id,
            PaymentHash = intent.PaymentHash,
            Status = status,
            BOLT11 = intent.LightningMetadata().Invoice,
            Preimage = intent.LightningMetadata().Preimage,
            CreatedAt = intent.CreatedAt,
            Amount = amount,
            AmountSent = locked,
            Fee = locked - amount,
        };
    }

    // BTCPay's payout processor marks Ok completed at once and retries Error, so anything short of a
    // settled payment answers Unknown and is followed through GetPayment.
    public static PayResponse ToPayResponse(ArkadeSwapIntent intent, BOLT11PaymentRequest pr, Network network)
    {
        var payment = ToPayment(intent, network);
        return new PayResponse
        {
            // Never Error: this runs after funding, and even a failed-looking status may be a Resolved fill.
            Result = payment.Status == LightningPaymentStatus.Complete ? PayResult.Ok : PayResult.Unknown,
            Details = new PayDetails
            {
                PaymentHash = pr.PaymentHash,
                Preimage = string.IsNullOrEmpty(payment.Preimage) ? null : new uint256(payment.Preimage),
                Status = payment.Status,
                FeeAmount = payment.Fee,
                TotalAmount = payment.AmountSent
            }
        };
    }

    public static PayResponse InFlight(BOLT11PaymentRequest? pr, string detail) =>
        new(PayResult.Unknown, detail)
        {
            Details = new PayDetails { PaymentHash = pr?.PaymentHash, Status = LightningPaymentStatus.Pending }
        };
}
