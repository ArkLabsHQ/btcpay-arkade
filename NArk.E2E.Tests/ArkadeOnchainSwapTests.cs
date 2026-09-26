using System.Text;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

// The onchain corridor end to end, against a live solver. Same stack and the same
// ARKADE_E2E_SOLVER_URL switch as ArkadeLightningCorridorTests; see its header for how to bring it up.
[Collection("Arkade Plugin Tests")]
[Trait("Category", "LightningCorridors")]
public class ArkadeOnchainSwapTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ArkadeOnchainSwapTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
    }

    private const long OrderSats = 25_000;

    [Fact]
    public async Task TheHtlcAsksThePayerForTheOrderAmount()
    {
        // Exact-in is what lets one BIP21 amount serve the HTLC, the Arkade address and the invoice at
        // once. Billed the order and paid the order: the solver's fee comes out of what the store nets.
        RequireSolver();
        var (storeId, client) = await SetUpStoreAsync();

        var invoice = await CreateInvoiceAsync(client, storeId);
        var swap = await PollForSwapAsync(await GetStoreWalletIdAsync(storeId)!);

        // The payer's leg is pinned to the order; the payout is that less the solver's fee, which a
        // regtest solver may waive entirely.
        Assert.Equal(OrderSats, swap.OfferAmount.Satoshi);
        Assert.True(swap.WantAmount.Satoshi <= OrderSats,
            $"the payout {swap.WantAmount.Satoshi} exceeds the {OrderSats} the payer was billed");

        var link = await PaymentLinkAsync(client, invoice.Id);
        Assert.Contains($"amount={Money.Satoshis(OrderSats).ToUnit(MoneyUnit.BTC)}", link);
        Assert.Contains("ark=", link);
    }

    [Fact]
    public async Task WithTheSenderPayingTheFee_NoSwapIsOffered()
    {
        // The swap only works while both legs want the same amount. Billing the payer the fee on top
        // would need a second amount no BIP21 can carry, so that store is offered boarding instead.
        RequireSolver();
        var (storeId, client) = await SetUpStoreAsync();
        await SetSenderPaysAsync(storeId);

        await CreateInvoiceAsync(client, storeId);
        await Task.Delay(TimeSpan.FromSeconds(20));

        var swaps = await ReadSwapsAsync(await GetStoreWalletIdAsync(storeId)!);
        Assert.DoesNotContain(swaps, s => s.Type == ArkadeSwapIntentType.OnchainToBtc);
    }

    [Fact]
    public async Task AFundedHtlc_KeepsTheInvoiceOpenAndCreditsItInFull()
    {
        // The money path, and the one that pays for the rest of this file. The payer sends the order
        // amount on L1; what lands on Arkade is smaller by the solver's fee, and the invoice must still
        // settle in full — BTCPay is told what the payer sent, as it is on the Lightning corridor.
        RequireSolver();
        var (storeId, client) = await SetUpStoreAsync();

        var invoice = await CreateInvoiceAsync(client, storeId);
        var walletId = await GetStoreWalletIdAsync(storeId);
        var swap = await PollForSwapAsync(walletId!);
        var htlc = HtlcAddressOf(await PaymentLinkAsync(client, invoice.Id));

        var expiryBefore = (await client.GetInvoice(invoice.Id)).ExpirationTime;

        await SendOnchainAsync(htlc, swap.OfferAmount.Satoshi);
        await MineAsync(3);

        // Held open while the swap runs: the claim can land well past a 15-minute checkout. A swap that
        // beats the watcher's tick leaves New on its own, which serves the same end — the invoice was
        // never dropped under a payer who paid. Only an invoice still New on its original expiry failed.
        var held = await PollAsync(
            async () =>
            {
                var current = await client.GetInvoice(invoice.Id);
                return current.ExpirationTime > expiryBefore || current.Status != InvoiceStatus.New;
            },
            TimeSpan.FromMinutes(2));
        Assert.True(held, "a funded HTLC must keep its invoice open");

        var settled = await PollAsync(async () =>
        {
            await MineAsync(1);
            var current = await client.GetInvoice(invoice.Id);
            return current.Status is InvoiceStatus.Settled or InvoiceStatus.Processing;
        }, TimeSpan.FromMinutes(6));

        Assert.True(settled, "the swap never credited its invoice");

        // Credited with what the payer sent, not the smaller amount that landed: anything less would
        // leave the invoice underpaid by the solver's fee.
        var paid = await client.GetInvoice(invoice.Id);
        Assert.Equal(OrderSats, (long)paid.Amount);
    }

    [Fact]
    public async Task RefreshingASwap_ReportsItsStatusWithoutDisturbingIt()
    {
        RequireSolver();
        var (storeId, client) = await SetUpStoreAsync();

        await CreateInvoiceAsync(client, storeId);
        var swap = await PollForSwapAsync(await GetStoreWalletIdAsync(storeId)!);

        var response = await GreenfieldAsync(
            HttpMethod.Post, $"/api/v1/stores/{storeId}/arkade/swaps/{swap.Id}/refresh");

        Assert.True(response.Ok, $"refresh returned {response.Status}: {await response.TextAsync()}");
        var body = JsonDocument.Parse(await response.TextAsync()).RootElement;
        Assert.Equal(swap.Id, body.GetProperty("swapId").GetString());
        Assert.Equal(swap.Status.ToString(), body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("reopened").GetBoolean());
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private async Task<(string StoreId, BTCPayServerClient Client)> SetUpStoreAsync()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        // An HD wallet, not an imported key: a single-key wallet derives one boarding address for every
        // invoice, so the handler offers neither boarding nor the swap on one.
        var storeId = await CreateStoreWithArkWalletAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);

        var settings = await GreenfieldAsync(
            HttpMethod.Patch, $"/api/v1/stores/{storeId}/arkade/wallet/settings",
            """{"onchainSwapEnabled":true,"boardingEnabled":true,"minBoardingAmountSats":1000}""");
        Assert.True(settings.Ok, $"enabling the swap returned {settings.Status}: {await settings.TextAsync()}");

        return (storeId, client);
    }

    private async Task SetSenderPaysAsync(string storeId)
    {
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var response = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/update-wallet-config").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                },
                Data = "command=toggle-swap-fee-payer",
            });

        Assert.True(response.Ok, $"toggling the fee payer returned {response.Status}");
    }

    private Task<InvoiceData> CreateInvoiceAsync(BTCPayServerClient client, string storeId) =>
        client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = OrderSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["ARKADE"] }
        });

    private async Task<string> PaymentLinkAsync(BTCPayServerClient client, string invoiceId)
    {
        var methods = await client.GetInvoicePaymentMethods(invoiceId);
        var arkade = methods.FirstOrDefault(m => m.PaymentMethodId.Contains("ARKADE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("the invoice carries no Arkade payment method");
        return arkade.PaymentLink ?? throw new InvalidOperationException("the Arkade method carries no payment link");
    }

    private static string HtlcAddressOf(string paymentLink)
    {
        var host = paymentLink["bitcoin:".Length..].Split('?')[0];
        Assert.False(string.IsNullOrEmpty(host), $"no onchain address in {paymentLink}");
        return host;
    }

    private async Task<IReadOnlyCollection<ArkadeSwapIntent>> ReadSwapsAsync(string walletId)
    {
        var storage = _fixture.ServerTester!.PayTester.ServiceProvider.GetRequiredService<IArkadeIntentStorage>();
        return await storage.GetArkadeSwapIntents(walletIds: [walletId]);
    }

    private async Task<ArkadeSwapIntent> PollForSwapAsync(string walletId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ReadSwapsAsync(walletId)).FirstOrDefault(s => s.Type == ArkadeSwapIntentType.OnchainToBtc) is { } swap)
                return swap;
            await Task.Delay(2_000);
        }

        throw new TimeoutException("no onchain swap was negotiated — is the solver reachable and funded?");
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> until, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await until()) return true;
            await Task.Delay(3_000);
        }

        return false;
    }

    private Task<IAPIResponse> GreenfieldAsync(HttpMethod method, string path, string? json = null)
    {
        var options = new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}")),
                ["Content-Type"] = "application/json",
            },
            Data = json ?? "{}",
        };

        var url = new Uri(ServerUri!, path).AbsoluteUri;
        return method == HttpMethod.Patch
            ? Page!.Context.APIRequest.PatchAsync(url, options)
            : Page!.Context.APIRequest.PostAsync(url, options);
    }

    private static string[] BitcoinCli =>
        ["bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123"];

    private static Task SendOnchainAsync(string address, long sats) =>
        DockerHelper.Exec("bitcoin", [.. BitcoinCli, "-rpcwallet=default", "sendtoaddress", address,
            Money.Satoshis(sats).ToUnit(MoneyUnit.BTC).ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static async Task MineAsync(int blocks)
    {
        var address = (await DockerHelper.Exec("bitcoin", [.. BitcoinCli, "-rpcwallet=default", "getnewaddress"])).Trim();
        await DockerHelper.Exec("bitcoin", [.. BitcoinCli, "generatetoaddress", blocks.ToString(), address]);
    }

    private static void RequireSolver()
    {
        var solver = Environment.GetEnvironmentVariable(SharedPluginTestFixture.SolverUrlVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(solver),
            $"no solver configured; set {SharedPluginTestFixture.SolverUrlVariable} " +
            "(see the header comment for the stack it needs)");
    }
}
