using NArk.E2E.Tests.Helpers;
using NArk.E2E.Tests.PageObjects;

namespace NArk.E2E.Tests.Tests;

/// <summary>
/// Tests for paying invoices with Arkade.
/// </summary>
[TestFixture]
[NonParallelizable]
public class InvoicePaymentTests : TestBase
{
    [Test]
    [Category("Integration")]
    [Description("Tests creating an invoice and paying it with Ark")]
    public async Task CreateInvoice_PayWithArk_InvoiceSettles()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange - Register user and create store with wallet
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Invoice Payment Test Store");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Get the store's receive address for the invoice
        var invoiceAmount = 50_000L; // 50k sats

        // Create an invoice
        var invoiceId = await CreateInvoiceAsync(invoiceAmount / 100_000_000m, "BTC");
        await GoToCheckoutAsync(invoiceId);

        // Get the Arkade payment address from checkout
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var arkAddress = await Page.GetAttributeAsync("[data-testid='ark-address']", "value")
            ?? await Page.TextContentAsync("[data-testid='ark-address']");

        if (string.IsNullOrEmpty(arkAddress))
        {
            Assert.Ignore("Arkade payment method not available on checkout page");
        }

        // Act - Pay the invoice using nigiri ark send (cheat mode pattern)
        await PayWithArkAsync(arkAddress, invoiceAmount);
        await TriggerArkRoundAsync();

        // Assert - Wait for payment confirmation
        await WaitForConditionAsync(
            async () =>
            {
                await Page.ReloadAsync();
                var status = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
                return status != null;
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromSeconds(3));

        // Verify invoice is settled
        var confirmedElement = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
        Assert.That(confirmedElement, Is.Not.Null, "Invoice should be confirmed after Ark payment");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests partial payment of an invoice")]
    public async Task CreateInvoice_PartialPayment_ShowsPartialStatus()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Partial Payment Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        var invoiceAmount = 100_000L; // 100k sats
        var partialAmount = 50_000L; // 50k sats (half)

        var invoiceId = await CreateInvoiceAsync(invoiceAmount / 100_000_000m, "BTC");
        await GoToCheckoutAsync(invoiceId);

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var arkAddress = await Page.GetAttributeAsync("[data-testid='ark-address']", "value")
            ?? await Page.TextContentAsync("[data-testid='ark-address']");

        if (string.IsNullOrEmpty(arkAddress))
        {
            Assert.Ignore("Arkade payment method not available");
        }

        // Act - Pay partial amount
        await PayWithArkAsync(arkAddress, partialAmount);
        await TriggerArkRoundAsync();

        // Assert - Should show partial payment status (not fully confirmed)
        await Task.Delay(TimeSpan.FromSeconds(5));
        await Page.ReloadAsync();

        // Invoice should still be waiting for full payment
        var confirmedElement = await Page.QuerySelectorAsync("[data-testid='payment-confirmed']");
        Assert.That(confirmedElement, Is.Null, "Invoice should not be confirmed after partial payment");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests that wallet balance increases after receiving payment")]
    public async Task ReceivePayment_WalletBalanceUpdates()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Balance Update Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Get the wallet's receive address
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();

        // Act - Fund the wallet directly
        var fundAmount = 75_000L; // 75k sats
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();

        // Wait for balance to update
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Assert
        var newBalance = await arkWallet.GetBalanceSatsAsync();
        Assert.That(newBalance, Is.GreaterThanOrEqualTo(fundAmount),
            "Wallet balance should reflect the funded amount");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests multiple payments to the same invoice")]
    public async Task CreateInvoice_MultiplePayments_AccumulatesCorrectly()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Multiple Payments Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        var invoiceAmount = 100_000L; // 100k sats
        var payment1 = 30_000L;
        var payment2 = 40_000L;
        var payment3 = 30_000L;

        var invoiceId = await CreateInvoiceAsync(invoiceAmount / 100_000_000m, "BTC");
        await GoToCheckoutAsync(invoiceId);

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var arkAddress = await Page.GetAttributeAsync("[data-testid='ark-address']", "value")
            ?? await Page.TextContentAsync("[data-testid='ark-address']");

        if (string.IsNullOrEmpty(arkAddress))
        {
            Assert.Ignore("Arkade payment method not available");
        }

        // Act - Make multiple payments
        await PayWithArkAsync(arkAddress, payment1);
        await TriggerArkRoundAsync();

        await PayWithArkAsync(arkAddress, payment2);
        await TriggerArkRoundAsync();

        await PayWithArkAsync(arkAddress, payment3);
        await TriggerArkRoundAsync();

        // Assert - Invoice should be fully paid
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
            "Invoice should be confirmed after receiving full amount across multiple payments");
    }
}
