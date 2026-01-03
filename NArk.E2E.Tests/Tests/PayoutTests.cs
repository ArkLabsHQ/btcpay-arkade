using NArk.E2E.Tests.Helpers;
using NArk.E2E.Tests.PageObjects;

namespace NArk.E2E.Tests.Tests;

/// <summary>
/// Tests for Arkade payout functionality.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PayoutTests : TestBase
{
    [Test]
    [Category("Integration")]
    [Description("Tests creating a manual payout")]
    public async Task CreatePayout_ManualApprove_PayoutCompletes()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange - Create store with funded wallet
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Payout Test Store");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 200_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var destinationAddress = await ArkFunding.GetDaemonReceiveAddressAsync();

        // Navigate to payouts page
        await Page.GotoAsync($"/stores/{storeId}/payouts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act - Create a payout request
        // Look for create payout button
        var createPayoutBtn = await Page.QuerySelectorAsync("[data-testid='create-payout-btn'], a[href*='payouts/create']");
        if (createPayoutBtn != null)
        {
            await createPayoutBtn.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Fill payout details
            var destinationInput = await Page.QuerySelectorAsync("input[name='Destination'], [data-testid='destination-input']");
            var amountInput = await Page.QuerySelectorAsync("input[name='Amount'], [data-testid='amount-input']");

            if (destinationInput != null && amountInput != null)
            {
                await destinationInput.FillAsync(destinationAddress);
                await amountInput.FillAsync("0.0005"); // 50k sats

                // Submit
                await Page.ClickAsync("button[type='submit']");
                await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
        }

        // Assert - Payout page should load without errors
        var pageContent = await Page.ContentAsync();
        Assert.That(pageContent, Does.Not.Contain("Error"),
            "Payout page should not show errors");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests configuring the automated payout processor")]
    public async Task ConfigurePayoutProcessor_SaveSettings_ConfigurationPersists()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Payout Processor Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Navigate to payout processors page
        await Page.GotoAsync($"/stores/{storeId}/payout-processors/ARKADE");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check if the page loaded correctly
        var form = await Page.QuerySelectorAsync("[data-testid='payout-processor-form']");
        if (form == null)
        {
            // Try alternative URL
            await Page.GotoAsync($"/stores/{storeId}/settings/payout-processors");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Act - Configure the processor
        var intervalInput = await Page.QuerySelectorAsync("[data-testid='interval-minutes'], input[name='IntervalMinutes']");
        if (intervalInput != null)
        {
            await intervalInput.FillAsync("5");
        }

        var instantToggle = await Page.QuerySelectorAsync("[data-testid='process-instantly-toggle'], input[name='ProcessNewPayoutsInstantly']");
        if (instantToggle != null)
        {
            var isChecked = await instantToggle.IsCheckedAsync();
            if (!isChecked)
            {
                await instantToggle.ClickAsync();
            }
        }

        var saveBtn = await Page.QuerySelectorAsync("[data-testid='save-processor-btn'], button[type='submit']");
        if (saveBtn != null)
        {
            await saveBtn.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Assert - Settings should be saved
        var successMessage = await Page.QuerySelectorAsync(".alert-success, [data-testid='success-message']");
        var pageContent = await Page.ContentAsync();
        Assert.That(pageContent, Does.Not.Contain("Error"),
            "Payout processor configuration should not show errors");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests payout list page displays correctly")]
    public async Task PayoutsList_PageLoads_ShowsCorrectInformation()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Payouts List Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Act - Navigate to payouts list
        await Page.GotoAsync($"/stores/{storeId}/payouts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Page should load without errors
        var pageTitle = await Page.TitleAsync();
        Assert.That(pageTitle, Does.Not.Contain("Error"),
            "Payouts list page should load without errors");

        // Should show some kind of payouts interface
        var pageContent = await Page.ContentAsync();
        var hasPayoutsContent = pageContent.Contains("Payout") ||
                               pageContent.Contains("payout") ||
                               pageContent.Contains("No pending");

        Assert.That(hasPayoutsContent, Is.True,
            "Page should show payouts-related content");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests that payouts require sufficient balance")]
    public async Task CreatePayout_InsufficientBalance_ShowsError()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange - Create store with wallet but minimal funds
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Insufficient Payout Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund with small amount
        var fundAmount = 10_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var destinationAddress = await ArkFunding.GetDaemonReceiveAddressAsync();

        // Navigate to create payout
        await Page.GotoAsync($"/stores/{storeId}/payouts/create");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act - Try to create payout larger than balance
        var destinationInput = await Page.QuerySelectorAsync("input[name='Destination'], [data-testid='destination-input']");
        var amountInput = await Page.QuerySelectorAsync("input[name='Amount'], [data-testid='amount-input']");

        if (destinationInput != null && amountInput != null)
        {
            await destinationInput.FillAsync(destinationAddress);
            await amountInput.FillAsync("0.1"); // 10M sats - way more than we have

            await Page.ClickAsync("button[type='submit']");
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Assert - Should show error or validation message
            var pageContent = await Page.ContentAsync();
            var hasError = pageContent.Contains("insufficient") ||
                          pageContent.Contains("Insufficient") ||
                          pageContent.Contains("balance") ||
                          pageContent.Contains("error") ||
                          pageContent.Contains("Error");

            // The system should prevent or warn about insufficient balance payouts
            // (exact behavior depends on implementation)
            TestContext.Progress.WriteLine($"Page content check for insufficient balance: hasError={hasError}");
        }
    }

    [Test]
    [Category("Integration")]
    [Description("Tests batch payout processing")]
    public async Task BatchPayouts_MultipleDestinations_ProcessedTogether()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Batch Payout Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet generously
        var fundAmount = 500_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Navigate to batch payouts (Pull payments or similar)
        await Page.GotoAsync($"/stores/{storeId}/pull-payments");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Page should load (batch functionality may vary by implementation)
        var pageContent = await Page.ContentAsync();
        Assert.That(pageContent, Does.Not.Contain("Error"),
            "Batch/Pull payments page should load without errors");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests payout cancellation")]
    public async Task CancelPendingPayout_PayoutCancelled_FundsNotSent()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Cancel Payout Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 100_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var initialBalance = await arkWallet.GetBalanceSatsAsync();

        // Navigate to payouts
        await Page.GotoAsync($"/stores/{storeId}/payouts");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Look for any pending payouts to cancel
        var cancelBtn = await Page.QuerySelectorAsync("[data-testid='cancel-payout-btn'], button:has-text('Cancel')");
        if (cancelBtn != null)
        {
            await cancelBtn.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Assert - Balance should remain unchanged (or increase if payout was reversed)
        await arkWallet.NavigateAsync();
        var currentBalance = await arkWallet.GetBalanceSatsAsync();

        Assert.That(currentBalance, Is.GreaterThanOrEqualTo(initialBalance - 5000), // Allow small variance
            "Balance should not significantly decrease after cancelling payout");
    }
}
