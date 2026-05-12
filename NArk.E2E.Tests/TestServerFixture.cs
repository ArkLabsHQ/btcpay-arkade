using System.Net;
using System.Net.Http;

namespace NArk.E2E.Tests;

/// <summary>
/// Fixture that waits for the test environment to be reachable. The
/// environment itself is provisioned outside the test runner:
/// <list type="bullet">
///   <item>nigiri/Ark stack (bitcoin, nbxplorer, arkd, boltz, lnd, fulmine)
///     started via <c>submodules/NNark/regtest/start-env.sh</c>.</item>
///   <item>Postgres started as a service container (CI) or by the dev
///     locally before invoking the tests.</item>
///   <item>BTCPayServer started in the background by the CI workflow with
///     the Arkade plugin DLL discoverable on its plugin path.</item>
/// </list>
/// This fixture used to <c>docker-compose up</c> and spawn BTCPay itself,
/// but those paths were unreliable (hardcoded compose location, LND
/// macaroon placeholder, postgres assumptions). Moving startup into the
/// CI workflow gives each piece its own log stream and clearer failure
/// signal.
/// </summary>
[SetUpFixture]
public class TestServerFixture
{
    /// <summary>BTCPayServer endpoint (CI binds to 0.0.0.0:14142).</summary>
    public static string ServerUrl =>
        Environment.GetEnvironmentVariable("BTCPAY_E2E_SERVER_URL") ?? "http://localhost:14142";

    /// <summary>arkd gRPC-rest gateway exposed by nigiri.</summary>
    public static string ArkDaemonUrl =>
        Environment.GetEnvironmentVariable("BTCPAY_E2E_ARKD_URL") ?? "http://localhost:7070";

    /// <summary>Boltz REST endpoint (Node.js service in the nigiri overlay).</summary>
    public static string BoltzUrl =>
        Environment.GetEnvironmentVariable("BTCPAY_E2E_BOLTZ_URL") ?? "http://localhost:9001";

    private static readonly HttpClient _httpClient = new();

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        TestContext.Progress.WriteLine("Waiting for test environment to be reachable...");

        await WaitForService(ArkDaemonUrl, "/v1/info", TimeSpan.FromSeconds(60),
            "Ark daemon (start the nigiri stack via submodules/NNark/regtest/start-env.sh)");

        await WaitForService(ServerUrl, "/", TimeSpan.FromMinutes(3),
            "BTCPayServer (CI starts it; locally run `dotnet run` in submodules/btcpayserver/BTCPayServer with the env vars from .github/workflows/e2e.yml)");

        TestContext.Progress.WriteLine("Test environment is reachable.");
    }

    private static async Task WaitForService(string baseUrl, string healthPath, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

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
                lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"{description} at {baseUrl} did not become ready within {timeout}. Last error: {lastError?.Message ?? "n/a"}");
    }
}
