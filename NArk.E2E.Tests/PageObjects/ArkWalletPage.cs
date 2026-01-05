namespace NArk.E2E.Tests.PageObjects;

/// <summary>
/// Page object for the Arkade wallet management UI.
/// Handles wallet creation, balance display, send/receive operations.
/// </summary>
public class ArkWalletPage
{
    private readonly IPage _page;
    private readonly string _storeId;

    public ArkWalletPage(IPage page, string storeId)
    {
        _page = page;
        _storeId = storeId;
    }

    /// <summary>
    /// Navigate to the Arkade wallet page for this store.
    /// </summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"/stores/{_storeId}/ark");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Check if wallet is already set up (StoreOverview page shows balance).
    /// </summary>
    public async Task<bool> IsWalletSetupAsync()
    {
        // If we're on InitialSetup page, wallet is not set up
        var createWalletOption = await _page.QuerySelectorAsync("[data-testid='hd-wallet-option']");
        if (createWalletOption != null)
            return false;

        // If we see a wallet balance or receive address, wallet is set up
        var walletBalance = await _page.QuerySelectorAsync("[data-testid='wallet-balance']");
        var receiveAddress = await _page.QuerySelectorAsync("[data-testid='receive-address']");
        return walletBalance != null || receiveAddress != null;
    }

    /// <summary>
    /// Create a new HD wallet.
    /// Note: BTCPay generates the wallet automatically, no mnemonic display.
    /// </summary>
    public async Task CreateNewHdWalletAsync()
    {
        // Click "Create a new wallet" option to expand it
        await _page.ClickAsync("[data-testid='hd-wallet-option']");

        // Wait for the form to expand
        await _page.WaitForSelectorAsync("[data-testid='create-wallet-btn']");

        // Click create wallet button
        await _page.ClickAsync("[data-testid='create-wallet-btn']");

        // Wait for redirect to StoreOverview with wallet set up
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Import an existing wallet from nsec key.
    /// </summary>
    public async Task ImportNsecAsync(string nsec)
    {
        // Click "Use an existing wallet" option to expand it
        await _page.ClickAsync("[data-testid='legacy-wallet-option']");

        // Wait for the form to expand
        await _page.WaitForSelectorAsync("[data-testid='nsec-input']");

        // Fill in the nsec
        await _page.FillAsync("[data-testid='nsec-input']", nsec);

        // Click import button
        await _page.ClickAsync("[data-testid='import-wallet-btn']");

        // Wait for redirect to StoreOverview
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Get the current wallet balance in satoshis.
    /// </summary>
    public async Task<long> GetBalanceSatsAsync()
    {
        var balanceText = await _page.TextContentAsync("[data-testid='wallet-balance']");
        if (balanceText == null)
            throw new Exception("Could not find wallet balance element");

        // Parse balance (format: "123456 sats")
        var cleanBalance = balanceText.Replace("sats", "").Trim();
        return long.Parse(cleanBalance);
    }

    /// <summary>
    /// Get the current wallet balance in BTC.
    /// </summary>
    public async Task<decimal> GetBalanceAsync()
    {
        var sats = await GetBalanceSatsAsync();
        return sats / 100_000_000m;
    }

    /// <summary>
    /// Get the wallet's receive address (shown on StoreOverview).
    /// </summary>
    public async Task<string> GetReceiveAddressAsync()
    {
        var address = await _page.GetAttributeAsync("[data-testid='receive-address']", "value");
        return address ?? throw new Exception("Could not get receive address");
    }

    /// <summary>
    /// Open transfer modal and send funds to a destination.
    /// </summary>
    public async Task SendAsync(string destination, decimal amount)
    {
        // Click send/transfer button to open modal
        await _page.ClickAsync("[data-testid='send-btn']");

        // Wait for modal
        await _page.WaitForSelectorAsync("[data-testid='transfer-modal']");

        // Fill destination
        await _page.FillAsync("[data-testid='destination-input']", destination);

        // Click continue to go to SpendOverview
        await _page.ClickAsync("[data-testid='confirm-send-btn']");

        // Wait for SpendOverview page
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // On SpendOverview, confirm the transfer
        var confirmBtn = await _page.QuerySelectorAsync("[data-testid='confirm-transfer-btn']");
        if (confirmBtn != null)
        {
            await confirmBtn.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }

    /// <summary>
    /// Wait for balance to reach expected amount in satoshis.
    /// </summary>
    public async Task WaitForBalanceAsync(long expectedSats, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var balance = await GetBalanceSatsAsync();
                if (balance >= expectedSats)
                    return;
            }
            catch
            {
                // Balance not available yet
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            await _page.ReloadAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        throw new TimeoutException($"Balance did not reach {expectedSats} sats within {timeout}");
    }

    /// <summary>
    /// Wait for balance to reach expected amount in BTC.
    /// </summary>
    public async Task WaitForBalanceAsync(decimal expectedBtc, TimeSpan timeout)
    {
        var expectedSats = (long)(expectedBtc * 100_000_000);
        await WaitForBalanceAsync(expectedSats, timeout);
    }

    /// <summary>
    /// Get the wallet's contract address (same as receive address).
    /// </summary>
    public async Task<string> GetContractAddressAsync()
    {
        return await GetReceiveAddressAsync();
    }

    /// <summary>
    /// Sync the wallet with the Ark daemon.
    /// </summary>
    public async Task SyncWalletAsync()
    {
        await _page.ClickAsync("[data-testid='sync-wallet-btn']");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Show the private key (opens modal).
    /// </summary>
    public async Task<string> GetPrivateKeyAsync()
    {
        await _page.ClickAsync("[data-testid='show-private-key-btn']");

        // Wait for the private key modal and get the value
        await _page.WaitForSelectorAsync("#privateKeyInput");
        var privateKey = await _page.GetAttributeAsync("#privateKeyInput", "value");

        // Close modal
        var closeBtn = await _page.QuerySelectorAsync("[data-testid='close-modal-btn']");
        if (closeBtn != null)
            await closeBtn.ClickAsync();

        return privateKey ?? throw new Exception("Could not get private key");
    }
}
