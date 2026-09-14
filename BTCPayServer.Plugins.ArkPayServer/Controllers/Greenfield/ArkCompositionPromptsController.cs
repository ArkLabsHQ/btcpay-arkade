using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.ArkadeIntents;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>Plugin-owned onchain ingress prompts; never replaces the core BTC-CHAIN handler.</summary>
[ApiController]
[Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public sealed class ArkCompositionPromptsController(ArkCompositionPromptService prompts,
    IArkadeIntentStorage intents) : ControllerBase
{
    [HttpPost("~/api/v1/stores/{storeId}/arkade/evm-settlement/invoices/{invoiceId}/onchain-prompt")]
    public async Task<IActionResult> CreateOnchain(string storeId, string invoiceId, CancellationToken cancellationToken)
    {
        if (HttpContext.GetStoreDataOrNull() is not { } store || store.Id != storeId) return NotFound();
        var invoice = await prompts.FindInvoiceAsync(invoiceId);
        if (invoice is null || invoice.StoreId != storeId) return NotFound();
        if (invoice.Status != BTCPayServer.Client.Models.InvoiceStatus.New || invoice.ExpirationTime <= DateTimeOffset.UtcNow)
            return Conflict(new { code = "invoice-not-payable", message = "The invoice has no active checkout window." });
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var prompt = invoice.GetPaymentPrompt(paymentMethodId);
        if (prompt is not { Activated: true, Currency: "BTC" })
            return Conflict(new { code = "payment-method-not-available", message = "The invoice has no active BTC onchain payment prompt." });
        try
        {
            var accounting = prompt.Calculate();
            var amountSats = Money.Coins(accounting.Due - accounting.PaymentMethodFee).Satoshi;
            if (amountSats <= 0)
                return Conflict(new { code = "invoice-not-payable", message = "The BTC onchain payment prompt has no amount due." });
            var route = await prompts.TryCreateAsync(store, invoice, paymentMethodId.ToString(), amountSats,
                invoice.ExpirationTime, cancellationToken: cancellationToken);
            if (route is null) return Conflict(new { code = "rail-not-enabled", message = "Onchain composition is not enabled for this store." });
            return Created($"/api/v1/stores/{storeId}/arkade/evm-settlement/routes/{route.RouteId}",
                ArkCompositionRouteData.From(await prompts.FindRouteAsync(storeId, route.RouteId, cancellationToken)
                    ?? throw new InvalidOperationException("The composed route was not indexed."),
                    await intents.GetArkadeSwapIntent(route.OutgoingSwapId, cancellationToken),
                    route.IngressSwapId is null ? null
                        : await intents.GetArkadeSwapIntent(route.IngressSwapId, cancellationToken),
                    prompts.ExecutionAvailable));
        }
        catch (ArkCompositionUnavailableException unavailable)
        {
            return Conflict(new { code = unavailable.Code, message = "No payable composition prompt was created. Inspect the public route journal before retrying." });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return Conflict(new { code = "composition-unavailable", message = "No payable composition prompt was created. Inspect the public route journal before retrying." });
        }
    }
}
