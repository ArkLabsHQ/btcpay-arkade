using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using NArk.E2E.Tests.Helpers;

namespace NArk.E2E.Tests;

/// <summary>
/// Base class for all E2E tests. Extends PlaywrightFixture with
/// common BTCPay-specific test utilities.
/// </summary>
[TestFixture]
[NonParallelizable]
public abstract class TestBase : PlaywrightFixture
{
    private static int _testCounter;
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    protected string TestStoreId { get; private set; } = null!;

    /// <summary>
    /// Ark funding helper instance for test use.
    /// </summary>
    protected ArkFundingHelper ArkFunding { get; } = new();

    /// <summary>
    /// Lightning helper instance for test use.
    /// </summary>
    protected LightningHelper Lightning { get; } = new();

    /// <summary>
    /// Register a new user with a unique email.
    /// </summary>
    protected async Task<(string Email, string Password)> RegisterNewUserAsync(bool isAdmin = false)
    {
        var email = $"test_{Interlocked.Increment(ref _testCounter)}@test.local";
        var password = "SuperSecurePassword123!";

        await Page.GotoAsync("/register");

        await Page.FillAsync("input[name='Email']", email);
        await Page.FillAsync("input[name='Password']", password);
        await Page.FillAsync("input[name='ConfirmPassword']", password);

        if (isAdmin)
        {
            // Check admin checkbox if visible (first user becomes admin automatically)
            var adminCheckbox = await Page.QuerySelectorAsync("input[name='IsAdmin']");
            if (adminCheckbox != null && await adminCheckbox.IsVisibleAsync())
            {
                await adminCheckbox.CheckAsync();
            }
        }

        await Page.ClickAsync("button[type='submit']");

        // Wait for redirect to dashboard or login
        await Page.WaitForURLAsync(url => !url.Contains("/register"));

        return (email, password);
    }

    /// <summary>
    /// Login with existing credentials.
    /// </summary>
    protected async Task LoginAsync(string email, string password)
    {
        await Page.GotoAsync("/login");

        await Page.FillAsync("input[name='Email']", email);
        await Page.FillAsync("input[name='Password']", password);
        await Page.ClickAsync("button[type='submit']");

        // Wait for redirect to dashboard
        await Page.WaitForURLAsync(url => !url.Contains("/login"));
    }

    /// <summary>
    /// Create a new store and return its ID.
    /// </summary>
    protected async Task<string> CreateStoreAsync(string storeName)
    {
        await Page.GotoAsync("/stores/create");

        await Page.FillAsync("input[name='Name']", storeName);
        await Page.ClickAsync("button[type='submit']");

        // Wait for redirect to store dashboard
        await Page.WaitForURLAsync(url => url.Contains("/stores/") && !url.Contains("/create"));

        // Extract store ID from URL
        var url = Page.Url;
        var storeIdMatch = System.Text.RegularExpressions.Regex.Match(url, @"/stores/([^/]+)");
        if (storeIdMatch.Success)
        {
            TestStoreId = storeIdMatch.Groups[1].Value;
            return TestStoreId;
        }

        throw new Exception($"Could not extract store ID from URL: {url}");
    }

    /// <summary>
    /// Navigate to the current test store's dashboard.
    /// </summary>
    protected async Task GoToStoreDashboardAsync()
    {
        if (string.IsNullOrEmpty(TestStoreId))
            throw new InvalidOperationException("No test store created. Call CreateStoreAsync first.");

        await Page.GotoAsync($"/stores/{TestStoreId}");
    }

    /// <summary>
    /// Create an invoice for the current store.
    /// </summary>
    protected async Task<string> CreateInvoiceAsync(decimal amount, string currency = "BTC")
    {
        await Page.GotoAsync($"/stores/{TestStoreId}/invoices/create");

        await Page.FillAsync("input[name='Amount']", amount.ToString());
        await Page.SelectOptionAsync("select[name='Currency']", currency);
        await Page.ClickAsync("button[type='submit']");

        // Wait for invoice page
        await Page.WaitForURLAsync(url => url.Contains("/invoices/"));

        // Extract invoice ID
        var url = Page.Url;
        var invoiceIdMatch = System.Text.RegularExpressions.Regex.Match(url, @"/invoices/([^/?]+)");
        if (invoiceIdMatch.Success)
        {
            return invoiceIdMatch.Groups[1].Value;
        }

        throw new Exception($"Could not extract invoice ID from URL: {url}");
    }

    /// <summary>
    /// Go to the checkout page for an invoice.
    /// </summary>
    protected async Task GoToCheckoutAsync(string invoiceId)
    {
        await Page.GotoAsync($"/i/{invoiceId}");
    }

    /// <summary>
    /// Pay an invoice using the cheat mode pattern (nigiri ark send).
    /// </summary>
    /// <param name="destination">The Ark address to pay to</param>
    /// <param name="amountSats">Amount in satoshis</param>
    protected async Task<string?> PayWithArkAsync(string destination, long amountSats)
    {
        return await ArkFunding.FundWalletAsync(destination, amountSats);
    }

    /// <summary>
    /// Mine Bitcoin blocks using nigiri.
    /// Matches the cheat mode MineBlock pattern.
    /// </summary>
    protected async Task MineBlocksAsync(int blockCount = 1)
    {
        await ArkFunding.MineBlocksAsync(blockCount);
    }

    /// <summary>
    /// Trigger an Ark round to process pending transactions.
    /// </summary>
    protected async Task TriggerArkRoundAsync()
    {
        await ArkFunding.TriggerRoundAsync();
    }

    /// <summary>
    /// Create a Lightning invoice on the test LND node.
    /// </summary>
    protected async Task<string> CreateLightningInvoiceAsync(long amountSats, string? memo = null)
    {
        return await Lightning.CreateInvoiceAsync(amountSats, memo, LightningHelper.LndNode.Lnd);
    }

    /// <summary>
    /// Pay a Lightning invoice from the Boltz LND node.
    /// </summary>
    protected async Task<string> PayLightningInvoiceAsync(string bolt11)
    {
        return await Lightning.PayInvoiceAsync(bolt11);
    }

    /// <summary>
    /// Wait for a condition to be true with polling.
    /// </summary>
    protected static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        pollInterval ??= TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(pollInterval.Value);
        }

        throw new TimeoutException($"Condition not met within {timeout}");
    }

    /// <summary>
    /// Check if the Ark daemon is available.
    /// </summary>
    protected async Task<bool> IsArkDaemonAvailableAsync()
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync("http://localhost:7070/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if the Boltz service is available.
    /// </summary>
    protected async Task<bool> IsBoltzAvailableAsync()
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync("http://localhost:9001/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Skip test if Ark daemon is not available.
    /// </summary>
    protected async Task SkipIfArkUnavailableAsync()
    {
        if (!await IsArkDaemonAvailableAsync())
        {
            Assert.Ignore("Ark daemon not available - skipping test");
        }
    }

    /// <summary>
    /// Skip test if Boltz service is not available.
    /// </summary>
    protected async Task SkipIfBoltzUnavailableAsync()
    {
        if (!await IsBoltzAvailableAsync())
        {
            Assert.Ignore("Boltz service not available - skipping test");
        }
    }

    /// <summary>
    /// Skip test if Lightning channels are not available.
    /// </summary>
    protected async Task SkipIfNoLightningChannelAsync()
    {
        if (!await Lightning.HasActiveChannelAsync())
        {
            Assert.Ignore("No active Lightning channel - skipping test");
        }
    }
}
