using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Times the click-to-paid latency of an Arkade invoice when funded via
/// the checkout cheat-mode (out-of-round <c>ark send</c> from inside the
/// arkd container, ~instant on arkd's side) and asserts the invoice
/// transitions to <c>Settled</c> within a regression threshold.
///
/// Two things this guards against:
///
/// 1. <b>Latency regression.</b> The current observed click-to-paid time
///    is ~6s, dominated by an unexplained gap between <c>UpsertVtxo</c>
///    firing <c>VtxosChanged</c> and BTCPay's invoice settling. We want a
///    test that fails loudly if a future change pushes this past
///    <see cref="LatencyThreshold"/>.
///
/// 2. <b>Missed-event regression.</b> If a refactor causes the plugin to
///    not deliver <c>VtxosChanged</c> at all, the invoice will hang at
///    <c>New</c> indefinitely. The test's outer timeout
///    (<see cref="HardTimeout"/>) catches that as a hard failure rather
///    than a flake.
///
/// The first iteration in a fresh test environment initialises the
/// in-arkd <c>ark</c> CLI and funds it via a single boarding+settle
/// (one batch round, kept out of the timed window). Subsequent
/// iterations on the same suite reuse the funded CLI.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class InvoicePaymentLatencyTests : PlaywrightBaseTest
{
    /// <summary>
    /// Outer timeout — if the invoice never settles inside this window,
    /// fail the test as a missed-event regression (not a flake).
    /// </summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pass-fail threshold for the timed latency. As of writing,
    /// observed real-world is ~6s; this is set with ~2x headroom against
    /// CI jitter. Tighten as we localise and fix the in-handler gap.
    /// </summary>
    private static readonly TimeSpan LatencyThreshold = TimeSpan.FromSeconds(12);

    private static readonly SemaphoreSlim _arkdCliSetupLock = new(1, 1);
    private static bool _arkdCliReady;

    private readonly SharedPluginTestFixture _fixture;

    public InvoicePaymentLatencyTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheatModePay_DirectArkTx_InvoiceSettlesWithinThreshold()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());

        // Funding the in-arkd ark CLI requires one batch round (~10s).
        // Deliberately do this BEFORE the timing window — we want to
        // measure the plugin's invoice-settle latency in isolation, not
        // the batch round.
        await EnsureArkdCliReadyAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            // SATS amount so the cheat extension routes a 5000-sat send
            // (matches what a manual cheat-pay button click would do).
            Amount = 5000m,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = new[] { "ARKADE" }
            }
        });
        Assert.False(string.IsNullOrEmpty(invoice.Id));

        // BTCPay enforces antiforgery on every UI controller POST (see
        // UIControllerAntiforgeryTokenAttribute registered in Startup);
        // missing token = 400 with empty body. Grab one off any page
        // that rendered a form — the Arkade overview always does.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        // Start the clock and trigger the cheat-mode pay. The
        // BTCPay route POST /i/{id}/test-payment invokes
        // ArkadeCheckoutCheatModeExtension.PayInvoice which shells out
        // `docker exec <arkd> ark send` — an out-of-round Ark tx that
        // arkd processes essentially instantly.
        var t0 = DateTimeOffset.UtcNow;
        // BTCPay's UIInvoiceController.TestPayment doesn't decorate its
        // request param with [FromBody], so ASP.NET MVC binds from form
        // data — not JSON. Sending JSON silently falls back to default
        // values (`PaymentMethodId="BTC"`) and the cheat-extension lookup
        // returns null. Use form-urlencoded to match the binder.
        var payResp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/i/{invoice.Id}/test-payment").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                    ["RequestVerificationToken"] = token
                },
                Data = "Amount=5000&CryptoCode=SATS&PaymentMethodId=ARKADE"
            });

        var payBody = await payResp.TextAsync();
        Assert.True(payResp.Ok,
            $"POST /i/{invoice.Id}/test-payment returned {payResp.Status}: {payBody}");

        // Poll the invoice status. We want to FAIL the test if the
        // invoice doesn't settle (missed event) AND record the elapsed
        // time when it does (latency regression). Tight loop —
        // resolution matters at the sub-second level.
        var deadline = t0 + HardTimeout;
        InvoiceStatus lastStatus = InvoiceStatus.New;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await client.GetInvoice(storeId, invoice.Id);
            lastStatus = current.Status;
            if (current.Status == InvoiceStatus.Settled)
            {
                var elapsed = DateTimeOffset.UtcNow - t0;
                TestLogs.LogInformation(
                    $"Invoice {invoice.Id} settled in {elapsed.TotalSeconds:F2}s " +
                    $"(threshold: {LatencyThreshold.TotalSeconds:F0}s)");
                Assert.True(elapsed <= LatencyThreshold,
                    $"Latency regression: invoice settled in {elapsed.TotalSeconds:F2}s, " +
                    $"threshold is {LatencyThreshold.TotalSeconds:F0}s. " +
                    "Pull the plugin debug log and look at the gap between " +
                    "`UpsertVtxo: inserted` and `invoice_paymentSettled` for the source.");
                return;
            }
            if (current.Status is InvoiceStatus.Invalid)
                Assert.Fail($"Invoice transitioned to Invalid (last: {lastStatus}) — payment was rejected, not just slow.");
            await Task.Delay(100);
        }

        // Timeout. This is the missed-event regression path — the cheat-mode
        // pay returned an OK txid (we asserted .Ok above) but the plugin's
        // VtxosChanged → OnVtxoChanged → AddPayment → InvoiceWatcher chain
        // never carried the payment through to Settled.
        Assert.Fail(
            $"Invoice {invoice.Id} never reached Settled within {HardTimeout.TotalSeconds:F0}s " +
            $"(last status: {lastStatus}). Likely a missed VtxosChanged event or a broken " +
            "subscriber on the path UpsertVtxo → OnVtxoChanged → paymentService.AddPayment.");
    }

    /// <summary>
    /// Ensures the in-arkd <c>ark</c> CLI is initialised (server URL +
    /// explorer + password) and has enough off-chain VTXO balance to fund
    /// at least one 5000-sat cheat-mode send. Idempotent across tests in
    /// the same suite via <see cref="_arkdCliReady"/>; the heavy work
    /// (boarding faucet + batch settle) runs at most once per process.
    /// </summary>
    private async Task EnsureArkdCliReadyAsync()
    {
        if (_arkdCliReady) return;
        await _arkdCliSetupLock.WaitAsync();
        try
        {
            if (_arkdCliReady) return;

            var container = await ResolveArkdContainerAsync();

            // Init the CLI if `ark config` reports it's missing.
            // Stderr text is the authoritative signal — exit code alone
            // isn't reliable across older builds.
            var configProbe = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", container, "ark", "config" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            var needsInit =
                !configProbe.IsSuccess ||
                configProbe.StandardError.Contains("not initialized", StringComparison.OrdinalIgnoreCase);
            if (needsInit)
            {
                // localhost:7070 = arkd gRPC inside its own container.
                // http://mempool_web/api = denigiri's Esplora-compatible REST
                // API (mempool), reachable on the arkd container's docker
                // network — the same endpoint denigiri's own `ark` client init
                // and arkd's EsploraURL use (replaces nigiri's chopsticks:3000).
                var initResult = await Cli.Wrap("docker")
                    .WithArguments(new[]
                    {
                        "exec", container, "ark", "init",
                        "--server-url", "http://localhost:7070",
                        "--explorer", "http://mempool_web/api",
                        "--password", "secret"
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (!initResult.IsSuccess)
                    throw new InvalidOperationException(
                        $"ark init failed (exit={initResult.ExitCode}): " +
                        $"stderr={initResult.StandardError.Trim()}, " +
                        $"stdout={initResult.StandardOutput.Trim()}");
            }

            // Already funded? Check off-chain balance.
            if (await GetArkdOffchainSatsAsync(container) >= 10_000)
            {
                _arkdCliReady = true;
                return;
            }

            // Fund the boarding address via nigiri faucet, mine to confirm,
            // then settle into off-chain VTXOs. The exit-script lock means
            // arkd needs the boarding UTXO confirmed (validateBoardingInput
            // calls IsTransactionConfirmed); skipping the mine causes
            // settle to fail with "fees (0) exceed total amount (0)".
            var receiveResult = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", container, "ark", "receive" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (!receiveResult.IsSuccess)
                throw new InvalidOperationException(
                    $"ark receive failed: {receiveResult.StandardError.Trim()}");
            using var receiveDoc = JsonDocument.Parse(receiveResult.StandardOutput);
            var boardingAddr = receiveDoc.RootElement.GetProperty("boarding_address").GetString()
                ?? throw new InvalidOperationException("ark receive returned no boarding_address");

            // 1 BTC is plenty for a suite-worth of 5000-sat cheat sends.
            // denigiri's bitcoin container (btcpayserver image, BITCOIN_NETWORK=regtest,
            // rpcuser=admin1/rpcpassword=123, single loaded wallet) replaces the old
            // `nigiri faucet`/`nigiri rpc --generate`; drive bitcoin-cli directly.
            var faucetResult = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "sendtoaddress", boardingAddr, "1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (!faucetResult.IsSuccess)
                throw new InvalidOperationException(
                    $"bitcoin-cli sendtoaddress {boardingAddr} failed: {faucetResult.StandardError.Trim()}");

            var mineResult = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "-generate", "6" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (!mineResult.IsSuccess)
                throw new InvalidOperationException(
                    $"bitcoin-cli -generate 6 failed: {mineResult.StandardError.Trim()}");

            // Retry settle to absorb the faucet/mine propagation race: when the
            // `nigiri faucet` TX hasn't reached bitcoind's mempool before we
            // mined, the boarding UTXO isn't in the confirmed chain and arkd
            // settles with no inputs ("fees (0) exceed total amount (0)").
            // Mine one extra block and retry — idempotent because settle is.
            const int settleAttempts = 5;
            CliWrap.Buffered.BufferedCommandResult settleResult = null!;
            for (var attempt = 1; attempt <= settleAttempts; attempt++)
            {
                settleResult = await Cli.Wrap("docker")
                    .WithArguments(new[] { "exec", container, "ark", "settle", "--password", "secret" })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (settleResult.IsSuccess) break;

                var racey = settleResult.StandardOutput.Contains(
                    "fees (0) exceed total amount (0)", StringComparison.Ordinal);
                if (!racey || attempt == settleAttempts)
                    throw new InvalidOperationException(
                        $"ark settle failed (exit={settleResult.ExitCode}, attempt={attempt}/{settleAttempts}): " +
                        $"stderr={settleResult.StandardError.Trim()}, " +
                        $"stdout={settleResult.StandardOutput.Trim()}");

                await Task.Delay(TimeSpan.FromSeconds(2));
                await Cli.Wrap("docker")
                    .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "-generate", "1" })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }

            if (await GetArkdOffchainSatsAsync(container) < 10_000)
                throw new InvalidOperationException(
                    "ark settle reported success but off-chain balance still under threshold; " +
                    "arkd may have delayed the commitment tx.");

            _arkdCliReady = true;
        }
        finally
        {
            _arkdCliSetupLock.Release();
        }
    }

    private static async Task<long> GetArkdOffchainSatsAsync(string container)
    {
        var balResult = await Cli.Wrap("docker")
            .WithArguments(new[] { "exec", container, "ark", "balance" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!balResult.IsSuccess) return 0;

        try
        {
            using var doc = JsonDocument.Parse(balResult.StandardOutput);
            if (doc.RootElement.TryGetProperty("offchain_balance", out var off) &&
                off.TryGetProperty("total", out var total) &&
                total.TryGetInt64(out var sats))
                return sats;
        }
        catch (JsonException)
        {
            // CLI not initialised yet — falls through to 0 so caller funds it.
        }
        return 0;
    }
}
