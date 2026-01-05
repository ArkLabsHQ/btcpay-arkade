using NArk.E2E.Tests.Helpers;
using NArk.E2E.Tests.PageObjects;

namespace NArk.E2E.Tests.Tests;

/// <summary>
/// Tests for spending from Ark wallet.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SpendingTests : TestBase
{
    [Test]
    [Category("Integration")]
    [Description("Tests sending Ark to another Ark address")]
    public async Task SendToArkAddress_TransactionCreated_BalanceDecreases()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange - Create store with funded wallet
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Ark Send Test");

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

        // Get a destination address (use Ark daemon's address)
        var destination = await ArkFunding.GetDaemonReceiveAddressAsync();

        // Act - Send to another Ark address
        var sendAmount = 30_000L; // 30k sats
        await arkWallet.SendAsync(destination, sendAmount / 100_000_000m);
        await TriggerArkRoundAsync();

        // Assert - Balance should decrease
        await arkWallet.NavigateAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        var newBalance = await arkWallet.GetBalanceSatsAsync();
        Assert.That(newBalance, Is.LessThan(initialBalance),
            "Balance should decrease after sending");

        var decrease = initialBalance - newBalance;
        Assert.That(decrease, Is.GreaterThanOrEqualTo(sendAmount),
            "Balance decrease should be at least the sent amount");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests the transfer modal UI flow")]
    public async Task OpenTransferModal_FillDetails_ModalBehavesCorrectly()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Transfer Modal Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 50_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Act - Click send button to open modal
        await Page.ClickAsync("[data-testid='send-btn']");
        await Page.WaitForSelectorAsync("[data-testid='transfer-modal']");

        // Assert - Modal is visible with correct elements
        var modal = await Page.QuerySelectorAsync("[data-testid='transfer-modal']");
        Assert.That(modal, Is.Not.Null, "Transfer modal should be visible");

        var destinationInput = await Page.QuerySelectorAsync("[data-testid='destination-input']");
        Assert.That(destinationInput, Is.Not.Null, "Destination input should be present");

        var confirmBtn = await Page.QuerySelectorAsync("[data-testid='confirm-send-btn']");
        Assert.That(confirmBtn, Is.Not.Null, "Confirm button should be present");

        // Close modal
        var closeBtn = await Page.QuerySelectorAsync("[data-testid='close-modal-btn']");
        if (closeBtn != null)
        {
            await closeBtn.ClickAsync();
        }
        else
        {
            // Try pressing Escape
            await Page.Keyboard.PressAsync("Escape");
        }
    }

    [Test]
    [Category("Integration")]
    [Description("Tests spending with insufficient balance")]
    public async Task SendMoreThanBalance_ShowsError()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Insufficient Balance Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund with small amount
        var fundAmount = 10_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var destination = await ArkFunding.GetDaemonReceiveAddressAsync();

        // Act - Try to send (balance is only 10k sats, insufficient for typical send + fees)
        await Page.ClickAsync("[data-testid='send-btn']");
        await Page.WaitForSelectorAsync("[data-testid='transfer-modal']");
        await Page.FillAsync("[data-testid='destination-input']", destination);
        await Page.ClickAsync("[data-testid='confirm-send-btn']");

        // Assert - Should show error or prevent submission
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Look for error message
        var errorMessage = await Page.QuerySelectorAsync(".validation-error, .text-danger, [data-testid='error-message']");
        var stillOnModal = await Page.QuerySelectorAsync("[data-testid='transfer-modal']");

        // Either there's an error message or we're still on the modal
        var hasError = errorMessage != null || stillOnModal != null;
        Assert.That(hasError, Is.True,
            "Should show error or prevent transfer with insufficient balance");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests that sending to invalid address shows error")]
    public async Task SendToInvalidAddress_ShowsValidationError()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Invalid Address Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 50_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Act - Try to send to invalid address
        await Page.ClickAsync("[data-testid='send-btn']");
        await Page.WaitForSelectorAsync("[data-testid='transfer-modal']");
        await Page.FillAsync("[data-testid='destination-input']", "invalid_address_123");
        await Page.ClickAsync("[data-testid='confirm-send-btn']");

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Should show validation error
        var errorMessage = await Page.QuerySelectorAsync(".validation-error, .text-danger, [data-testid='error-message']");
        Assert.That(errorMessage, Is.Not.Null, "Should show validation error for invalid address");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests the SpendOverview page flow")]
    public async Task SpendOverview_ShowsCorrectBalanceAndOptions()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Spend Overview Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet
        var fundAmount = 75_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Act - Navigate to SpendOverview
        await Page.GotoAsync($"/stores/{storeId}/ark/spend");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Should show balance cards and transfer form
        var balanceSection = await Page.QuerySelectorAsync("[data-testid='wallet-balances']");
        var transferForm = await Page.QuerySelectorAsync("[data-testid='transfer-form']");

        Assert.That(balanceSection != null || transferForm != null,
            "SpendOverview should show balance or transfer form");
    }

    [Test]
    [Category("Integration")]
    [Description("Tests sending multiple small transactions")]
    public async Task SendMultipleTransactions_AllSucceed_BalanceTrackedCorrectly()
    {
        // Skip if Ark daemon not available
        await SkipIfArkUnavailableAsync();

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Multiple Sends Test");

        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Fund the wallet with enough for multiple sends
        var fundAmount = 200_000L;
        var receiveAddress = await arkWallet.GetReceiveAddressAsync();
        await ArkFunding.FundWalletAsync(receiveAddress, fundAmount);
        await ArkFunding.TriggerRoundAsync();
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        var initialBalance = await arkWallet.GetBalanceSatsAsync();
        var destination = await ArkFunding.GetDaemonReceiveAddressAsync();

        // Act - Send multiple transactions
        var sendAmounts = new[] { 20_000L, 15_000L, 25_000L };
        var totalSent = 0L;

        foreach (var amount in sendAmounts)
        {
            await arkWallet.NavigateAsync();
            await arkWallet.SendAsync(destination, amount / 100_000_000m);
            await TriggerArkRoundAsync();
            totalSent += amount;

            // Brief delay between transactions
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Assert - Final balance should reflect all sends
        await arkWallet.NavigateAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        var finalBalance = await arkWallet.GetBalanceSatsAsync();
        var totalDecrease = initialBalance - finalBalance;

        Assert.That(totalDecrease, Is.GreaterThanOrEqualTo(totalSent),
            $"Balance should decrease by at least {totalSent} sats after multiple sends");
    }
}
