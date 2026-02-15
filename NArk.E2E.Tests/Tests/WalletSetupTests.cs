using NArk.E2E.Tests.Helpers;
using NArk.E2E.Tests.PageObjects;

namespace NArk.E2E.Tests.Tests;

/// <summary>
/// Tests for Arkade wallet setup functionality.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WalletSetupTests : TestBase
{
    [Test]
    public async Task RegisterAndCreateStore_NavigateToArkWallet_ShowsSetupPage()
    {
        // Arrange - Register new user
        await RegisterNewUserAsync(isAdmin: true);

        // Act - Create store
        var storeId = await CreateStoreAsync("Test Ark Store");

        // Navigate to Ark wallet page
        var arkWallet = new ArkWalletPage(Page, storeId);
        await arkWallet.NavigateAsync();

        // Assert - Should show setup page (wallet not configured yet)
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var isSetup = await arkWallet.IsWalletSetupAsync();
        Assert.That(isSetup, Is.False, "Wallet should not be set up initially");

        // Should see the wallet setup options
        var hdOption = await Page.QuerySelectorAsync("[data-testid='hd-wallet-option']");
        var legacyOption = await Page.QuerySelectorAsync("[data-testid='legacy-wallet-option']");
        Assert.That(hdOption, Is.Not.Null, "Should see HD wallet creation option");
        Assert.That(legacyOption, Is.Not.Null, "Should see legacy wallet import option");
    }

    [Test]
    public async Task CreateNewWallet_WalletIsSetUp_ShowsOverview()
    {
        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("HD Wallet Test Store");
        var arkWallet = new ArkWalletPage(Page, storeId);

        // Act
        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Assert - Should now be on StoreOverview with wallet set up
        var isSetup = await arkWallet.IsWalletSetupAsync();
        Assert.That(isSetup, Is.True, "Wallet should be set up after creation");

        // Should see the receive address
        var receiveAddress = await Page.QuerySelectorAsync("[data-testid='receive-address']");
        Assert.That(receiveAddress, Is.Not.Null, "Should see receive address after wallet setup");
    }

    [Test]
    public async Task CreateNewWallet_CanRetrievePrivateKey()
    {
        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Private Key Test Store");
        var arkWallet = new ArkWalletPage(Page, storeId);

        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Act - Get private key
        var privateKey = await arkWallet.GetPrivateKeyAsync();

        // Assert - Should have an nsec
        Assert.That(privateKey, Is.Not.Null.And.Not.Empty);
        Assert.That(privateKey, Does.StartWith("nsec"), "Private key should be an nsec");
    }

    [Test]
    public async Task CreateWallet_GetReceiveAddress_AddressReturned()
    {
        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Receive Address Test");
        var arkWallet = new ArkWalletPage(Page, storeId);

        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        // Act
        var address = await arkWallet.GetReceiveAddressAsync();

        // Assert
        Assert.That(address, Is.Not.Null.And.Not.Empty);
        // Regtest Ark addresses start with "tark1" or similar taproot format
        Assert.That(address, Does.Match(@"^(ark|tark|bcrt)1[a-z0-9]+$"),
            "Address should be a valid Ark or Bitcoin taproot address");
    }

    [Test]
    [Category("Integration")]
    [Description("Requires running Ark daemon - skipped in CI without full environment")]
    public async Task FundWallet_BalanceUpdates()
    {
        // Skip if Ark daemon not available
        if (!await IsArkDaemonAvailableAsync())
        {
            Assert.Ignore("Ark daemon not available - skipping integration test");
        }

        // Arrange
        await RegisterNewUserAsync(isAdmin: true);
        var storeId = await CreateStoreAsync("Fund Test Store");
        var arkWallet = new ArkWalletPage(Page, storeId);
        var funding = new ArkFundingHelper();

        await arkWallet.NavigateAsync();
        await arkWallet.CreateNewHdWalletAsync();

        var contractAddress = await arkWallet.GetContractAddressAsync();

        // Act - Fund the wallet
        var fundAmount = 50_000L; // 50k sats
        await funding.FundWalletAsync(contractAddress, fundAmount);
        await funding.TriggerRoundAsync();

        // Wait for balance to update
        await arkWallet.WaitForBalanceAsync(fundAmount, TimeSpan.FromSeconds(30));

        // Assert
        var newBalance = await arkWallet.GetBalanceSatsAsync();
        Assert.That(newBalance, Is.GreaterThanOrEqualTo(fundAmount));
    }

}
