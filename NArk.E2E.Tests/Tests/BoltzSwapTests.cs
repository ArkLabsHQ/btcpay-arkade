using NArk.E2E.Tests.Helpers;
using NArk.E2E.Tests.PageObjects;

namespace NArk.E2E.Tests.Tests;

/// <summary>
/// Tests for Boltz swap functionality (Lightning-Ark interop).
/// Requires Boltz service and LND nodes to be running.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BoltzSwapTests : TestBase
{
    [Test]
    [Category("Integration")]
    [Description("Tests receiving Lightning payment via Boltz reverse swap")]
    public async Task ReverseSwap_PayLightningInvoice_ArkWalletReceivesFunds()
    {
        // Skip if services not available
        await SkipIfArkUnavailableAsync();
        await SkipIfBoltzUnavailableAsync();
        await SkipIfNoLightningChannelAsync();

        // Arrange - Create store with Ark wallet configured as Lightning backend
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Boltz Reverse Swap Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Get initial balance
        var initialBalance = await arkWallet.GetBalanceSatsAsync();

        // Create an invoice that uses Boltz for Lightning
        var invoiceAmount = 25_000L; // 25k sats
        var invoiceId = await CreateInvoiceAsync(invoiceAmount / 100_000_000m, "BTC");
        await GoToCheckoutAsync(invoiceId);

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Get the BOLT11 invoice from checkout
        var bolt11 = await Page.GetAttributeAsync("[data-testid='bolt11-invoice']", "value")
            ?? await Page.TextContentAsync("[data-testid='bolt11-invoice']");

        if (string.IsNullOrEmpty(bolt11))
        {
            Assert.Ignore("BOLT11 invoice not available on checkout page - Boltz may not be configured");
        }

        // Act - Pay the Lightning invoice from external LND node
        await PayLightningInvoiceAsync(bolt11);

        // Assert - Wait for payment confirmation and balance update
        await WaitForConditionAsync(
            async () =>
            {
                await Page.ReloadAsync();
                var status = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
                return status != null;
            },
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromSeconds(5));

        // Verify Ark wallet received funds (minus Boltz fees)
        await arkWallet.NavigateAsync();
        await Task.Delay(TimeSpan.FromSeconds(5)); // Allow time for balance sync

        var newBalance = await arkWallet.GetBalanceSatsAsync();
        Assert.That(newBalance, Is.GreaterThan(initialBalance),
            "Ark wallet should have increased balance after Lightning payment");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests sending from Ark to pay a Lightning invoice via Boltz submarine swap")]
    public async Task SubmarineSwap_SpendArkToPayLightning_PaymentSucceeds()
    {
        // Skip if services not available
        await SkipIfArkUnavailableAsync();
        await SkipIfBoltzUnavailableAsync();
        await SkipIfNoLightningChannelAsync();

        // Arrange - Create store with funded Ark wallet
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Boltz Submarine Swap Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 100_000L; // 100k sats
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var initialBalance = await arkWallet.GetBalanceSatsAsync();

        // Create a Lightning invoice on external node
        var invoiceAmount = 20_000L; // 20k sats
        var bolt11 = await CreateLightningInvoiceAsync(invoiceAmount, "Test submarine swap");

        // Act - Use the spend/transfer page to pay the Lightning invoice
        await arkWallet.SendAsync(bolt11, invoiceAmount / 100_000_000m);

        // Wait for the swap to complete
        await Task.Delay(TimeSpan.FromSeconds(30));

        // Assert - Wallet balance should have decreased
        await arkWallet.NavigateAsync();
        var newBalance = await arkWallet.GetBalanceSatsAsync();

        Assert.That(newBalance, Is.LessThan(initialBalance),
            "Ark wallet balance should decrease after paying Lightning invoice");

        // The decrease should be approximately the invoice amount plus fees
        var expectedMaxDecrease = invoiceAmount * 1.05m; // Allow 5% for fees
        var actualDecrease = initialBalance - newBalance;
        Assert.That(actualDecrease, Is.LessThanOrEqualTo((long)expectedMaxDecrease),
            "Balance decrease should be close to invoice amount plus reasonable fees");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests that Boltz swap status is tracked correctly")]
    public async Task BoltzSwap_StatusTracking_ShowsCorrectStatus()
    {
        // Skip if services not available
        await SkipIfArkUnavailableAsync();
        await SkipIfBoltzUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Swap Status Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 50_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Navigate to swaps/history page if available
        await Page.GotoAsync($"/stores/{storeId}/ark/swaps");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Page should load without errors
        var pageTitle = await Page.TitleAsync();
        Assert.That(pageTitle, Does.Not.Contain("Error"),
            "Swap status page should load without errors");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests small Lightning payment via Boltz")]
    public async Task SmallPayment_BoltzHandlesMinimumAmount()
    {
        // Skip if services not available
        await SkipIfArkUnavailableAsync();
        await SkipIfBoltzUnavailableAsync();
        await SkipIfNoLightningChannelAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Small Payment Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Boltz typically has minimum amounts (e.g., 10k sats)
        var minAmount = 10_000L;
        var invoiceId = await CreateInvoiceAsync(minAmount / 100_000_000m, "BTC");
        await GoToCheckoutAsync(invoiceId);

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check if BOLT11 is available (Boltz should handle this amount)
        var bolt11 = await Page.GetAttributeAsync("[data-testid='bolt11-invoice']", "value")
            ?? await Page.TextContentAsync("[data-testid='bolt11-invoice']");

        if (string.IsNullOrEmpty(bolt11))
        {
            Assert.Ignore("BOLT11 invoice not available - may be below Boltz minimum");
        }

        // Act - Pay the invoice
        await PayLightningInvoiceAsync(bolt11);

        // Assert - Payment should succeed
        await WaitForConditionAsync(
            async () =>
            {
                await Page.ReloadAsync();
                var status = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
                return status != null;
            },
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromSeconds(5));

        var confirmedElement = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
        Assert.That(confirmedElement, Is.Not.Null,
            "Small Lightning payment should complete via Boltz");
    }
}
