namespace NArk.E2E.Tests;

/// <summary>
/// Base fixture for Playwright browser automation.
/// Provides browser and page instances for each test.
/// </summary>
public class PlaywrightFixture
{
    protected IPlaywright Playwright { get; private set; } = null!;
    protected IBrowser Browser { get; private set; } = null!;
    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;

    protected string ServerUrl => TestServerFixture.ServerUrl;

    [SetUp]
    public async Task SetupBrowser()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        // Use Chromium for consistency with CI
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false",
            SlowMo = 50 // Slight delay for stability
        });

        Context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = ServerUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        });

        // Set default timeout for all operations
        Context.SetDefaultTimeout(30000);

        Page = await Context.NewPageAsync();
    }

    [TearDown]
    public async Task TeardownBrowser()
    {
        // Take screenshot on failure for debugging
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            var screenshotPath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"{TestContext.CurrentContext.Test.Name}_failure.png");

            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
            TestContext.AddTestAttachment(screenshotPath);
        }

        await Page.CloseAsync();
        await Context.CloseAsync();
        await Browser.CloseAsync();
        Playwright.Dispose();
    }

    /// <summary>
    /// Navigate to a path relative to the server URL.
    /// </summary>
    protected async Task NavigateToAsync(string path)
    {
        await Page.GotoAsync(path);
    }

    /// <summary>
    /// Wait for an element to appear and be visible.
    /// </summary>
    protected async Task WaitForElementAsync(string selector, TimeSpan? timeout = null)
    {
        await Page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = (float?)timeout?.TotalMilliseconds
        });
    }

    /// <summary>
    /// Click an element and wait for navigation if expected.
    /// </summary>
    protected async Task ClickAndNavigateAsync(string selector)
    {
        await Page.ClickAsync(selector);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
