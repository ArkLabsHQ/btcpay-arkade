namespace NArk.E2E.Tests.PageObjects;

/// <summary>
/// Page object for BTCPay checkout page.
/// Handles payment method selection and payment confirmation.
/// </summary>
public class CheckoutPage
{
    private readonly IPage _page;

    public CheckoutPage(IPage page)
    {
        _page = page;
    }

    /// <summary>
    /// Navigate directly to an invoice checkout.
    /// </summary>
    public async Task NavigateAsync(string invoiceId)
    {
        await _page.GotoAsync($"/i/{invoiceId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Select the Arkade payment method.
    /// </summary>
    public async Task SelectArkadePaymentMethodAsync()
    {
        // Look for Arkade payment option
        await _page.ClickAsync("[data-payment-method='ARKADE']");
        await _page.WaitForSelectorAsync("[data-testid='arkade-qr']");
    }

    /// <summary>
    /// Get available payment methods.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailablePaymentMethodsAsync()
    {
        var methods = await _page.QuerySelectorAllAsync("[data-payment-method]");
        var methodNames = new List<string>();

        foreach (var method in methods)
        {
            var name = await method.GetAttributeAsync("data-payment-method");
            if (name != null)
                methodNames.Add(name);
        }

        return methodNames;
    }

    /// <summary>
    /// Get the Ark payment address for current invoice.
    /// </summary>
    public async Task<string> GetArkPaymentAddressAsync()
    {
        var address = await _page.TextContentAsync("[data-testid='ark-address']");
        return address ?? throw new Exception("Could not get Ark payment address");
    }

    /// <summary>
    /// Get the BOLT11 Lightning invoice (when Arkade uses Boltz).
    /// </summary>
    public async Task<string> GetBolt11InvoiceAsync()
    {
        var bolt11 = await _page.TextContentAsync("[data-testid='bolt11-invoice']");
        return bolt11 ?? throw new Exception("Could not get BOLT11 invoice");
    }

    /// <summary>
    /// Get the BIP21 payment URI.
    /// </summary>
    public async Task<string> GetPaymentUriAsync()
    {
        var uri = await _page.GetAttributeAsync("[data-testid='payment-uri']", "data-uri");
        return uri ?? throw new Exception("Could not get payment URI");
    }

    /// <summary>
    /// Wait for payment confirmation.
    /// </summary>
    public async Task WaitForPaymentConfirmationAsync(TimeSpan timeout)
    {
        await _page.WaitForSelectorAsync(
            "[data-testid='payment-confirmed'], .payment-confirmed, .invoice-status-settled",
            new PageWaitForSelectorOptions
            {
                Timeout = (float)timeout.TotalMilliseconds
            });
    }

    /// <summary>
    /// Check if invoice is in paid/settled state.
    /// </summary>
    public async Task<bool> IsPaymentConfirmedAsync()
    {
        var confirmed = await _page.QuerySelectorAsync("[data-testid='payment-confirmed']");
        return confirmed != null;
    }

    /// <summary>
    /// Get the current invoice status.
    /// </summary>
    public async Task<string> GetInvoiceStatusAsync()
    {
        var statusElement = await _page.QuerySelectorAsync("[data-testid='invoice-status']");
        if (statusElement != null)
        {
            var status = await statusElement.TextContentAsync();
            return status ?? "Unknown";
        }

        // Try alternative selectors
        var altStatus = await _page.QuerySelectorAsync(".invoice-status");
        if (altStatus != null)
        {
            var status = await altStatus.TextContentAsync();
            return status ?? "Unknown";
        }

        return "Unknown";
    }

    /// <summary>
    /// Get the amount due in BTC.
    /// </summary>
    public async Task<decimal> GetAmountDueAsync()
    {
        var amountText = await _page.TextContentAsync("[data-testid='amount-due']");
        if (amountText == null)
            throw new Exception("Could not get amount due");

        var cleanAmount = amountText.Replace("BTC", "").Trim();
        return decimal.Parse(cleanAmount);
    }

    /// <summary>
    /// Refresh the checkout page to check for payment updates.
    /// </summary>
    public async Task RefreshAsync()
    {
        await _page.ReloadAsync();
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
