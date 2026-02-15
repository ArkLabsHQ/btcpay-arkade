namespace NArk.E2E.Tests.PageObjects;

/// <summary>
/// Page object for BTCPay payouts management.
/// Handles creating, approving, and processing payouts.
/// </summary>
public class PayoutsPage
{
    private readonly IPage _page;
    private readonly string _storeId;

    public PayoutsPage(IPage page, string storeId)
    {
        _page = page;
        _storeId = storeId;
    }

    /// <summary>
    /// Navigate to the payouts page.
    /// </summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"/stores/{_storeId}/payouts");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Create a new payout.
    /// </summary>
    public async Task<string> CreatePayoutAsync(string destination, decimal amount, string payoutMethod = "ARKADE")
    {
        await _page.ClickAsync("[data-testid='create-payout-btn']");

        await _page.FillAsync("[data-testid='destination-input']", destination);
        await _page.FillAsync("[data-testid='amount-input']", amount.ToString("F8"));

        // Select payout method
        await _page.SelectOptionAsync("[data-testid='payout-method']", payoutMethod);

        await _page.ClickAsync("[data-testid='submit-payout-btn']");

        // Wait for payout to be created
        await _page.WaitForSelectorAsync("[data-testid='payout-created-success']");

        // Get payout ID from success message or redirect
        var payoutId = await _page.TextContentAsync("[data-testid='payout-id']");
        return payoutId ?? throw new Exception("Could not get payout ID");
    }

    /// <summary>
    /// Approve a pending payout.
    /// </summary>
    public async Task ApprovePayoutAsync(string payoutId)
    {
        // Select the payout
        await _page.ClickAsync($"[data-payout-id='{payoutId}'] [data-testid='select-payout']");

        // Click approve button
        await _page.ClickAsync("[data-testid='approve-selected-btn']");

        // Confirm approval
        await _page.ClickAsync("[data-testid='confirm-approve-btn']");

        await _page.WaitForSelectorAsync("[data-testid='approval-success']");
    }

    /// <summary>
    /// Process approved payouts (trigger payment).
    /// </summary>
    public async Task ProcessPayoutAsync(string payoutId)
    {
        // Select the payout
        await _page.ClickAsync($"[data-payout-id='{payoutId}'] [data-testid='select-payout']");

        // Click process button
        await _page.ClickAsync("[data-testid='process-selected-btn']");

        // Confirm processing
        await _page.ClickAsync("[data-testid='confirm-process-btn']");

        // Wait for redirect to spend page or success
        await _page.WaitForURLAsync(url =>
            url.Contains("/ark/spend") || url.Contains("success"));
    }

    /// <summary>
    /// Get the status of a payout.
    /// </summary>
    public async Task<string> GetPayoutStatusAsync(string payoutId)
    {
        await NavigateAsync(); // Refresh page

        var status = await _page.TextContentAsync($"[data-payout-id='{payoutId}'] [data-testid='payout-status']");
        return status ?? "Unknown";
    }

    /// <summary>
    /// Get count of pending payouts.
    /// </summary>
    public async Task<int> GetPendingPayoutCountAsync()
    {
        var pendingPayouts = await _page.QuerySelectorAllAsync("[data-payout-status='pending']");
        return pendingPayouts.Count;
    }

    /// <summary>
    /// Wait for payout to reach a specific status.
    /// </summary>
    public async Task WaitForPayoutStatusAsync(string payoutId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var status = await GetPayoutStatusAsync(payoutId);
            if (status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"Payout {payoutId} did not reach status '{expectedStatus}' within {timeout}");
    }
}
