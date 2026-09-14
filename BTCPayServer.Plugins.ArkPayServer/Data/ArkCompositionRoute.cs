using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>
/// Store-scoped index from a BTCPay invoice to its SDK-composed EVM route.
/// Holds no swap state: quotes, funding, EVM proofs and lifecycle all live in
/// the SDK's <c>ArkadeSwapIntents</c> storage and are read live from there.
/// </summary>
public sealed class ArkCompositionRoute
{
    private ArkCompositionRoute() { }

    /// <summary>Independent identity for this rail and renewal.</summary>
    [Key]
    public Guid RouteId { get; private set; }

    /// <summary>Immutable owning store.</summary>
    public string StoreId { get; private set; } = "";

    /// <summary>Invoice this route was prompted for, if any.</summary>
    public string? InvoiceId { get; private set; }

    /// <summary>ARKADE, BTC-LN or BTC-CHAIN.</summary>
    public string PaymentMethodId { get; private set; } = "";

    /// <summary>Wallet that owns the SDK intents.</summary>
    public string WalletId { get; private set; } = "";

    /// <summary>SDK outgoing (Arkade-to-EVM) RFQ identity.</summary>
    public string OutgoingSwapId { get; private set; } = "";

    /// <summary>SDK ingress RFQ identity, absent for direct Arkade routes.</summary>
    public string? IngressSwapId { get; private set; }

    /// <summary>SDK-generated H, shared by both legs.</summary>
    public string PaymentHash { get; private set; } = "";

    /// <summary>Exact BTC amount required by L, in sats.</summary>
    public long BaseAmountSats { get; private set; }

    /// <summary>Customer-facing destination (address or BOLT11) shown at prompt time.</summary>
    public string? CustomerDestination { get; private set; }

    /// <summary>Last Unix second at which the prompt may be shown.</summary>
    public long? CheckoutExpiresAt { get; private set; }

    /// <summary>UTC route creation time.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public static ArkCompositionRoute Create(string storeId, string? invoiceId, string paymentMethodId,
        string walletId, string outgoingSwapId, string? ingressSwapId, string paymentHash,
        long baseAmountSats, string? customerDestination, long? checkoutExpiresAt, DateTimeOffset createdAt) =>
        new()
        {
            RouteId = Guid.NewGuid(),
            StoreId = storeId,
            InvoiceId = invoiceId,
            PaymentMethodId = paymentMethodId,
            WalletId = walletId,
            OutgoingSwapId = outgoingSwapId,
            IngressSwapId = ingressSwapId,
            PaymentHash = paymentHash,
            BaseAmountSats = baseAmountSats,
            CustomerDestination = customerDestination,
            CheckoutExpiresAt = checkoutExpiresAt,
            CreatedAt = createdAt
        };

    public void AttachInvoice(string invoiceId)
    {
        if (InvoiceId is not null && InvoiceId != invoiceId)
            throw new InvalidOperationException("A route cannot change its invoice.");
        InvoiceId = invoiceId;
    }
}
