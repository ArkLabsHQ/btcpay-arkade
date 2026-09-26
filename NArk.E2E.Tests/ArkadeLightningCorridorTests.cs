using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Client;
using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using Microsoft.Playwright;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

// Needs a solver, claim daemon and emulator beside regtest; skipped unless ARKADE_E2E_SOLVER_URL is set.
// An http:// solver URL selects the HTTP transport; ws:// selects a relay and also needs ARKADE_E2E_SOLVER_PUBKEY.
// The solver's Lightning backend is the `lnd` container and lnd refuses self-payments, so every invoice
// these tests pay, or hand over to be paid, is minted on `lnd-peer`.
//
// 1. Stack. VTXO_TREE_EXPIRY must exceed the solver's 7200s refund horizon or it silently refuses to fund:
//    INTENT_SOLVER_IMAGE=ghcr.io/arkade-os/intent-solver:0.2.0 ARKD_VTXO_TREE_EXPIRY=15360 \
//    ARKD_UNILATERAL_EXIT_DELAY=512 ARKD_PUBLIC_UNILATERAL_EXIT_DELAY=512 ARKD_BOARDING_EXIT_DELAY=2048 \
//    ARKD_CHECKPOINT_EXIT_DELAY=1536 node submodules/NNark/regtest/regtest.mjs start --clean \
//    --profile intent-solver,covclaimd
// 2. dotnet run --project ConfigBuilder/ConfigBuilder.csproj, and install chromium via the built playwright.ps1.
// 3. TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;admin1:123" TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/" \
//    TESTS_POSTGRES="Host=localhost;Port=39372;Database=btcpay_e2e_test;Username=postgres" TESTS_HOSTNAME=127.0.0.1 \
//    ARKADE_E2E_SOLVER_URL=http://127.0.0.1:8787 dotnet test --project NArk.E2E.Tests/NArk.E2E.Tests.csproj \
//    --filter-trait "Category=LightningCorridors"
//
// The "corridors" entry in .github/workflows/e2e.yml runs exactly this.
[Collection("Arkade Plugin Tests")]
[Trait("Category", "LightningCorridors")]
public class ArkadeLightningCorridorTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ArkadeLightningCorridorTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    // LUD-06 wallets refuse an invoice above the approved amount, so the spread must come out of the payout.
    [Fact]
    public async Task CreateInvoice_BillsThePayerTheOrderAmount()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, orderSats);
        var decoded = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);
        var invoiceSats = (long)decoded.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

        Assert.Equal(orderSats, invoiceSats);
    }

    [Fact]
    public async Task PaidLightningInvoice_CreditsTheStoreOnArkade()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var before = await ReadAvailableBalanceSatsAsync();

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, orderSats);

        // The hold clears only once our claim reveals the preimage, so returning means the round trip completed.
        await DockerHelper.Exec("lnd-peer", ["lncli", "--network=regtest", "payinvoice", "--force", bolt11]);

        var after = await PollForBalanceAsync(storeId, before + 1, TimeSpan.FromMinutes(5));

        Assert.True(after > before, $"balance did not grow after the invoice was paid ({before} -> {after})");
    }

    [Fact]
    public async Task PayLightningInvoice_SettlesAtThePayee()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundOverTheReceiveCorridorAsync(client, storeId, 50_000);

        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 30_000, TimeSpan.FromMinutes(2));
        Assert.NotEmpty(outpoints);

        var bolt11 = await DockerHelper.CreateLndInvoice(amtSats: 20_000, expirySecs: 1800, container: "lnd-peer");

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var settled = await SpendToLightningAsync(storeId, bolt11, outpoints, token);
        Assert.True(settled, "the payee never saw the invoice settle");

        var intent = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Fulfilled);
        Assert.False(string.IsNullOrEmpty(intent.SpentTxid), "a fulfilled send swap must record the spend that settled it");

        // Not asserted: Preimage is empty because the SDK's ProvesFill recovers it from the claim witness
        // and discards it, so LightningPayment.Preimage is null on every Arkade payment. SDK-side gap.
    }


    // An order past the solver's float must yield no BOLT11: a payer could pay into a swap nobody funds.
    // Funding the lockup is not the payment: the solver has yet to pay the invoice, so a 200 here
    // would book a payout the payee was never paid for.
    [Fact]
    public async Task FundingASend_IsReportedAsInFlight()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        await FundOverTheReceiveCorridorAsync(client, storeId, 50_000);
        await PollForSpendableCoinsAsync(storeId, "LightningInvoice", 30_000, TimeSpan.FromMinutes(2));

        var bolt11 = await DockerHelper.CreateLndInvoice(amtSats: 20_000, expirySecs: 1800, container: "lnd-peer");

        var response = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/api/v1/stores/{storeId}/lightning/BTC/invoices/pay").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Basic " + Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}")),
                    ["Content-Type"] = "application/json",
                },
                Data = $$"""{"BOLT11":"{{bolt11}}"}""",
            });

        Assert.Equal(202, response.Status);
    }

    [Fact]
    public async Task CreateInvoice_ForMoreThanTheSolverCanFund_HandsOutNoInvoice()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, storeId, 5_000_000_000L));

        // Either BTCPay refuses the method or no Lightning destination appears; a BOLT11 is not acceptable.
        Assert.True(
            ex is GreenfieldAPIException or TimeoutException,
            $"expected a refusal or an absent destination, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task CreateInvoice_BelowDust_IsRefusedWithoutNegotiating()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, storeId, 100));
    }

    [Fact]
    public async Task UnpaidInvoice_CreditsNothingAndStaysUnfulfilled()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var before = await ReadAvailableBalanceSatsAsync();

        await CreateLightningInvoiceAsync(client, storeId, 25_000);

        // Long enough for a funded lockup to have been claimed had one existed.
        await Task.Delay(TimeSpan.FromSeconds(45));

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var after = await ReadAvailableBalanceSatsAsync();
        Assert.Equal(before, after);

        var intents = await ReadIntentsAsync(walletId!);
        Assert.DoesNotContain(intents, i => i.Status == ArkadeSwapIntentStatus.Fulfilled);
    }

    // The row holds the only usable preimage, so it must exist before the invoice is payable.
    [Fact]
    public async Task ReceiveSwap_IsRecordedBeforePaying_ThenReachesFulfilled()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, 25_000);

        var recorded = Assert.Single(await ReadIntentsAsync(walletId!, ArkadeSwapIntentType.LightningToBtc));
        var lightning = recorded.LightningMetadata();
        Assert.Equal(bolt11, lightning.Invoice);
        Assert.False(string.IsNullOrEmpty(lightning.Preimage), "the preimage must be stored before the invoice is payable");
        Assert.NotEqual(ArkadeSwapIntentStatus.Fulfilled, recorded.Status);

        await DockerHelper.Exec("lnd-peer", ["lncli", "--network=regtest", "payinvoice", "--force", bolt11]);

        var settled = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Fulfilled);
        Assert.Equal(recorded.Id, settled.Id);
    }

    // The Greenfield test checks what was minted; this checks what a LUD-06 wallet accepts, since
    // BTCPay's UILNURLController passes the solver's invoice through without comparing amounts.
    [Fact]
    public async Task LnurlCallback_ReturnsAnInvoiceForTheAmountAsked()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = orderSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["BTC-LNURL"] }
        });

        var payRequest = await GetJsonAsync($"/BTC/lnurl/pay/i/{invoice.Id}");
        var callback = payRequest.GetProperty("callback").GetString();
        Assert.False(string.IsNullOrEmpty(callback), "the LNURL pay request carried no callback");

        var minSendable = payRequest.GetProperty("minSendable").GetInt64();
        var maxSendable = payRequest.GetProperty("maxSendable").GetInt64();
        var askMsat = orderSats * 1000;
        Assert.InRange(askMsat, minSendable, maxSendable);

        var separator = callback!.Contains('?') ? "&" : "?";
        var callbackResponse = await GetJsonAsync($"{callback}{separator}amount={askMsat}");

        var pr = callbackResponse.TryGetProperty("pr", out var prValue) ? prValue.GetString() : null;
        Assert.False(string.IsNullOrEmpty(pr),
            $"the callback returned no invoice: {callbackResponse}");

        var decoded = BOLT11PaymentRequest.Parse(pr!, Network.RegTest);
        Assert.Equal(askMsat, (long)decoded.MinimumAmount.MilliSatoshi);
    }

    // Characterisation, not a fix: the solver sets a fixed ~30 min expiry and drops BTCPay's, so a 60-min
    // checkout has an unpayable tail. Capping the checkout is a product decision.
    [Fact]
    public async Task CheckoutWindow_OutlastsTheInvoiceItOffers()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var checkoutWindow = TimeSpan.FromMinutes(60);

        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = 25_000,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = ["BTC-LN"],
                Expiration = checkoutWindow
            }
        });

        var bolt11 = await ReadLightningDestinationAsync(client, invoice.Id);
        var decoded = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);

        var checkoutEnds = invoice.ExpirationTime;
        var invoiceEnds = decoded.ExpiryDate;

        var unpayable = checkoutEnds - invoiceEnds;

        Assert.True(
            unpayable > TimeSpan.Zero,
            $"the invoice now outlives the {checkoutWindow.TotalMinutes:F0}-minute checkout " +
            $"(checkout ends {checkoutEnds:u}, invoice {invoiceEnds:u}) — the constraint this test " +
            "documents has changed");

        TestLogs.LogInformation(
            $"checkout window {checkoutWindow.TotalMinutes:F0} min, invoice window " +
            $"{(invoiceEnds - DateTimeOffset.UtcNow).TotalMinutes:F0} min, " +
            $"unpayable tail {unpayable.TotalMinutes:F0} min");
    }

    // Both connection strings name the same wallet; without the capability check the second store could spend it.
    [Fact]
    public async Task AStoreWithoutTheSpendKey_TakesPaymentsButCannotPay()
    {
        RequireSolver();

        var (ownerStoreId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(ownerStoreId);
        Assert.False(string.IsNullOrEmpty(walletId));

        var borrowerStoreId = await CreateStore("borrower");
        await client.UpdateStorePaymentMethod(borrowerStoreId, "BTC-LN", new UpdatePaymentMethodRequest
        {
            Enabled = true,
            Config = JObject.FromObject(new { connectionString = $"type=arkade;wallet-id={walletId}" })
        });

        var bolt11 = await CreateLightningInvoiceAsync(client, borrowerStoreId, 25_000);
        Assert.False(string.IsNullOrEmpty(bolt11), "a store without the spend-key could not take a payment");

        var payee = await DockerHelper.CreateLndInvoice(amtSats: 20_000, expirySecs: 1800, container: "lnd-peer");
        var refused = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/api/v1/stores/{borrowerStoreId}/lightning/BTC/invoices/pay").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Basic " + Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}")),
                    ["Content-Type"] = "application/json",
                },
                Data = $$"""{"BOLT11":"{{payee}}"}""",
            });

        Assert.False(refused.Ok,
            $"a store without the spend-key paid from somebody else's wallet (HTTP {refused.Status})");
    }

    // The claim needs the wallet's signature and nothing else does it for us yet, so the invoice is
    // refused rather than taken and left unclaimable.
    [Fact]
    public async Task AWatchOnlyWallet_IsRefusedAnInvoiceItCouldNotClaim()
    {
        RequireSolver();

        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithWatchOnlyWalletAsync($"tr({new Key().PubKey.ToHex()})");
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await EnableLightningAsync(storeId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, storeId, 25_000));

        // A refusal from the server, not a timeout waiting for a destination that never appears —
        // those have different causes and only one of them is this guard.
        Assert.IsType<GreenfieldAPIException>(ex);
    }

    [Fact]
    public async Task LightningSwapsPage_ShowsASwapTheStoreJustMade()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, 25_000);
        var paymentHash = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).PaymentHash!.ToString();

        await GoToUrl($"/plugins/ark/stores/{storeId}/lightning-swaps");
        var page = await Page!.ContentAsync();

        // The page lists every corridor's swaps now, so its heading is just "Swaps".
        Assert.Contains(">Swaps<", page);

        // truncate-center splits the hash for display; its ends survive in the markup.
        Assert.Contains(paymentHash[..8], page, StringComparison.OrdinalIgnoreCase);
    }

    // The hold invoice is never settled, so the solver genuinely cannot claim our lockup.
    // Mining ahead is not enough: refund *execution* is gated on median-time-past, but the monitor decides a
    // swap is refundable by the wall clock, so setmocktime never triggers it. Hence a real wait on a solver
    // with a short horizon (ARKADE_E2E_SHORT_REFUND_HORIZON). Refund logic itself is unit-tested in the SDK.
    [Fact]
    [Trait("Category", "LightningCorridorsDestructive")]
    public async Task UnfilledSendSwap_IsRefundedOnceItsLocktimePasses()
    {
        RequireSolver();

        var horizonRaw = Environment.GetEnvironmentVariable("ARKADE_E2E_SHORT_REFUND_HORIZON");
        Assert.SkipWhen(
            !int.TryParse(horizonRaw, out var horizonSeconds) || horizonSeconds <= 0,
            "needs a solver quoting a short refund horizon; set ARKADE_E2E_SHORT_REFUND_HORIZON to it " +
            "(mining ahead does not substitute)");

        var (storeId, _) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId!, 200_000);

        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 30_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var fundedBalance = await ReadAvailableBalanceSatsAsync();
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var preimage = RandomNumberGenerator.GetBytes(32);
        var paymentHash = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
        await DockerHelper.Exec(
            "lnd-peer", ["lncli", "--network=regtest", "addholdinvoice", paymentHash, "20000"]);

        var held = await ReadHoldInvoiceAsync(paymentHash);
        Assert.False(string.IsNullOrEmpty(held), "lnd did not return a hold invoice");

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(held!)}"
            });
        Assert.True(resp.Ok, $"build-intent (LN) returned {resp.Status}: {await resp.TextAsync()}");

        var funded = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending);
        var locktime = funded.RefundLocktime
            ?? throw new InvalidOperationException("the swap recorded no refund locktime");

        // Necessary but not sufficient: see the wall-clock note above.
        await AdvanceChainPastAsync(locktime);

        var waitFor = TimeSpan.FromSeconds(horizonSeconds) + TimeSpan.FromMinutes(2);

        var refunded = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Cancelled, waitFor);
        Assert.False(string.IsNullOrEmpty(refunded.SpentTxid), "a refunded swap must record the spend that returned the sats");

        var returned = await PollForBalanceAsync(storeId, fundedBalance - 20_000, TimeSpan.FromMinutes(5));
        Assert.True(returned > 0, "the refund did not restore a spendable balance");
    }

    // The same toggle the overview page posts; only the corridor tests need the store's LN method set.
    private async Task EnableLightningAsync(string storeId)
    {
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var response = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/enable-ln").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["RequestVerificationToken"] = token },
            });

        Assert.True(response.Ok, $"enabling Lightning returned {response.Status}: {await response.TextAsync()}");
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(ServerUri!, url).AbsoluteUri;

        var response = await Page!.Context.APIRequest.GetAsync(absolute);
        var body = await response.TextAsync();

        Assert.True(response.Ok, $"GET {absolute} returned {response.Status}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> ReadHoldInvoiceAsync(string paymentHash)
    {
        var raw = await DockerHelper.Exec(
            "lnd-peer", ["lncli", "--network=regtest", "lookupinvoice", paymentHash]);

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("payment_request", out var pr) ? pr.GetString() : null;
    }

    // Mined well past the locktime, since median-time-past trails the tip.
    private static async Task AdvanceChainPastAsync(long locktime)
    {
        var target = locktime + (long)TimeSpan.FromMinutes(30).TotalSeconds;
        string[] cli = ["bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123"];

        await DockerHelper.Exec("bitcoin", [.. cli, "setmocktime", target.ToString()]);

        var address = (await DockerHelper.Exec(
            "bitcoin", [.. cli, "-rpcwallet=default", "getnewaddress"])).Trim();

        await DockerHelper.Exec("bitcoin", [.. cli, "generatetoaddress", "15", address]);
    }

    private async Task<(string StoreId, BTCPayServerClient Client)> SetUpStoreAsync()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await EnableLightningAsync(storeId);
        return (storeId, new BTCPayServerClient(ServerUri, CreatedUser, Password));
    }

    // Funding over the corridor instead of redeeming a note: a note has to settle into a batch before
    // it can be spent, which costs minutes and stalls outright whenever a round is failing. A paid
    // invoice lands as an ordinary offchain payment, spendable as soon as it is claimed.
    private async Task FundOverTheReceiveCorridorAsync(
        BTCPayServerClient client, string storeId, long amountSats)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var before = await ReadAvailableBalanceSatsAsync();

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, amountSats);
        await DockerHelper.Exec("lnd-peer", ["lncli", "--network=regtest", "payinvoice", "--force", bolt11]);

        var after = await PollForBalanceAsync(storeId, before + 1, TimeSpan.FromMinutes(5));
        Assert.True(after > before, $"funding over the corridor did not land ({before} -> {after})");
    }

    private async Task<string> CreateLightningInvoiceAsync(
        BTCPayServerClient client, string storeId, long amountSats)
    {
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amountSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = ["BTC-LN"]
            }
        });

        return await ReadLightningDestinationAsync(client, invoice.Id);
    }

    private static async Task<string> ReadLightningDestinationAsync(
        BTCPayServerClient client, string invoiceId)
    {
        var invoice = new { Id = invoiceId };

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var methods = await client.GetInvoicePaymentMethods(invoice.Id);
            var lightning = methods.FirstOrDefault(m =>
                m.PaymentMethodId.Contains("LN", StringComparison.OrdinalIgnoreCase));

            if (lightning?.Destination is { Length: > 0 } bolt11) return bolt11;

            await Task.Delay(2_000);
        }

        throw new TimeoutException(
            $"invoice {invoice.Id} never got a Lightning destination — the solver did not quote. " +
            "Check the solver is reachable and funded.");
    }

    private async Task<bool> SpendToLightningAsync(
        string storeId, string bolt11, IEnumerable<string> outpoints, string token)
    {
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(bolt11)}"
            });

        Assert.True(resp.Ok, $"build-intent (LN) returned {resp.Status}: {await resp.TextAsync()}");

        var paymentHash = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest)
            .PaymentHash!.ToString();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var raw = await DockerHelper.Exec(
                "lnd-peer", ["lncli", "--network=regtest", "lookupinvoice", paymentHash]);

            if (InvoiceState(raw) is "SETTLED") return true;

            await Task.Delay(3_000);
        }

        return false;
    }

    private async Task<IReadOnlyCollection<ArkadeSwapIntent>> ReadIntentsAsync(
        string walletId, ArkadeSwapIntentType? type = null)
    {
        var storage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<IArkadeIntentStorage>();

        var all = await storage.GetArkadeSwapIntents(walletIds: [walletId]);
        return type is null ? all : all.Where(i => i.Type == type).ToList();
    }

    private async Task<ArkadeSwapIntent> PollForIntentStatusAsync(
        string walletId,
        ArkadeSwapIntentType type,
        ArkadeSwapIntentStatus status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        ArkadeSwapIntentStatus? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = (await ReadIntentsAsync(walletId, type))
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefault();

            last = match?.Status;
            if (match is not null && match.Status == status) return match;

            await Task.Delay(2_000);
        }

        throw new TimeoutException(
            $"no {type} swap for wallet {walletId} reached {status} (last seen: {last?.ToString() ?? "none"}).");
    }

    // Parsed, not substring-matched: an unpaid invoice's reply contains "settled": false.
    private static string? InvoiceState(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RequireSolver()
    {
        var solver = Environment.GetEnvironmentVariable(SharedPluginTestFixture.SolverUrlVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(solver),
            $"no solver configured; set {SharedPluginTestFixture.SolverUrlVariable} " +
            "(see the header comment for the stack it needs)");
    }
}
