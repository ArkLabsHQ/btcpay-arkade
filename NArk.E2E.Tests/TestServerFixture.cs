using System.Net;
using System.Net.Http;

namespace NArk.E2E.Tests;

/// <summary>
/// Fixture that manages the test environment:
/// - Ensures Ark stack is running (docker-compose.ark.yml)
/// - Starts BTCPayServer with the Arkade plugin loaded
/// - Provides URLs for tests to connect to
/// </summary>
[SetUpFixture]
public class TestServerFixture
{
    public static string ServerUrl => "http://localhost:14142";
    public static string ArkDaemonUrl => "http://localhost:7070";
    public static string BoltzUrl => "http://localhost:9001";

    private static Process? _btcpayProcess;
    private static readonly HttpClient _httpClient = new();

    private static string SolutionDir => Path.GetFullPath(
        Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        TestContext.Progress.WriteLine("Starting test environment...");

        // Check if Ark stack is running
        await EnsureArkStackRunning();

        // Start BTCPayServer with plugin
        await StartBTCPayServer();

        // Wait for BTCPay to be ready
        await WaitForBTCPayReady();

        TestContext.Progress.WriteLine("Test environment ready.");
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        TestContext.Progress.WriteLine("Stopping test environment...");

        if (_btcpayProcess is { HasExited: false })
        {
            _btcpayProcess.Kill(entireProcessTree: true);
            await _btcpayProcess.WaitForExitAsync();
        }

        _btcpayProcess?.Dispose();
        TestContext.Progress.WriteLine("Test environment stopped.");
    }

    private static async Task EnsureArkStackRunning()
    {
        TestContext.Progress.WriteLine("Checking Ark stack...");

        try
        {
            var response = await _httpClient.GetAsync($"{ArkDaemonUrl}/v1/info");
            if (response.IsSuccessStatusCode)
            {
                TestContext.Progress.WriteLine("Ark daemon is running.");
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Ark not running, try to start it
        }

        TestContext.Progress.WriteLine("Ark stack not running. Starting via docker-compose...");

        var result = await Cli.Wrap("docker-compose")
            .WithArguments(["-f", "docker-compose.ark.yml", "up", "-d"])
            .WithWorkingDirectory(SolutionDir)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to start Ark stack: {result.StandardError}");
        }

        // Wait for Ark daemon to be ready
        await WaitForService(ArkDaemonUrl, "/v1/info", TimeSpan.FromSeconds(60));
        TestContext.Progress.WriteLine("Ark stack started.");
    }

    private static async Task StartBTCPayServer()
    {
        TestContext.Progress.WriteLine("Starting BTCPayServer with Arkade plugin...");

        // Check if already running
        try
        {
            var response = await _httpClient.GetAsync(ServerUrl);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Found)
            {
                TestContext.Progress.WriteLine("BTCPayServer already running.");
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Not running, start it
        }

        var btcpayProjectPath = Path.Combine(SolutionDir, "submodules", "btcpayserver", "BTCPayServer");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build",
            WorkingDirectory = btcpayProjectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Set environment variables for regtest
        startInfo.Environment["BTCPAY_NETWORK"] = "regtest";
        startInfo.Environment["BTCPAY_BIND"] = "0.0.0.0:14142";
        startInfo.Environment["BTCPAY_ROOTPATH"] = "/";
        startInfo.Environment["BTCPAY_DEBUGLOG"] = "debug.log";
        startInfo.Environment["BTCPAY_POSTGRES"] =
            "Host=localhost;Port=5432;Database=btcpay_e2e_test;Username=postgres;Password=postgres";
        startInfo.Environment["BTCPAY_BTCEXPLORERURL"] = "http://localhost:24444/";
        startInfo.Environment["BTCPAY_BTCLIGHTNING"] = "type=lnd-rest;server=https://localhost:8080/;macaroonfilepath=<path>;allowinsecure=true";

        _btcpayProcess = Process.Start(startInfo);

        if (_btcpayProcess == null)
        {
            throw new Exception("Failed to start BTCPayServer process");
        }

        // Log output for debugging
        _btcpayProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) TestContext.Progress.WriteLine($"[BTCPay] {e.Data}");
        };
        _btcpayProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) TestContext.Error.WriteLine($"[BTCPay ERROR] {e.Data}");
        };

        _btcpayProcess.BeginOutputReadLine();
        _btcpayProcess.BeginErrorReadLine();
    }

    private static async Task WaitForBTCPayReady()
    {
        TestContext.Progress.WriteLine("Waiting for BTCPayServer to be ready...");
        await WaitForService(ServerUrl, "/", TimeSpan.FromSeconds(120));
        TestContext.Progress.WriteLine("BTCPayServer is ready.");
    }

    private static async Task WaitForService(string baseUrl, string healthPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{baseUrl}{healthPath}");
                if (response.IsSuccessStatusCode ||
                    response.StatusCode == HttpStatusCode.Found ||
                    response.StatusCode == HttpStatusCode.Redirect)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Service not ready yet
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"Service at {baseUrl} did not become ready within {timeout}");
    }
}
