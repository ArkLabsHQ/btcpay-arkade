using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.E2E.Tests;

[Collection(ComposedEvmTestCollection.Name)]
[Trait("Category", "ComposedEvm")]
public sealed class ComposedEvmPaymentTests(ComposedEvmInfrastructureFixture infrastructure,
    SharedPluginTestFixture btcpay, ITestOutputHelper output) : PlaywrightBaseTest(output)
{
    private const string WatchOnlyDescriptor = "tr(0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798)";

    [Fact]
    public async Task WatchOnlyMerchant_ArkadeInvoice_SettlesOnlyAfterExactErc20Receipt()
    {
        Assert.SkipWhen(!infrastructure.IsEnabled,
            $"Set {ComposedEvmInfrastructureFixture.EnableVariable}=1 and provide the regtest root and secrets file.");
        var cancellationToken = TestContext.Current.CancellationToken;
        await infrastructure.StartAsync(cancellationToken);
        btcpay.Initialize(this);
        await infrastructure.AssertBtCPayReadinessAsync(btcpay.ServerTester!.PayTester.HttpClient, cancellationToken);
        await InitializePlaywright(btcpay.ServerTester);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var store = await client.CreateStore(new CreateStoreRequest { Name = "Composed EVM merchant" }, cancellationToken);
        var merchant = await CreateStoreScopedClientAsync(client, store.Id, cancellationToken);
        using var greenfield = CreateGreenfieldClient(merchant);
        await SetupWatchOnlyWalletAsync(greenfield, store.Id, enableLightning: false, cancellationToken);
        await infrastructure.ConfigureMerchantSettlementAsync(greenfield, store.Id, ["ARKADE"], cancellationToken);

        var wallet = await GetJsonAsync(greenfield, $"api/v1/stores/{store.Id}/arkade/wallet", cancellationToken);
        infrastructure.AssertPublicProjectionSafe(wallet.GetRawText());
        Assert.False(wallet.GetProperty("signerAvailable").GetBoolean());
        Assert.False(wallet.GetProperty("isOwnedByStore").GetBoolean());

        var before = await infrastructure.GetErc20BalanceAsync(infrastructure.MerchantEvmAddress, cancellationToken);
        var invoice = await merchant.CreateInvoice(store.Id, new CreateInvoiceRequest
        {
            Amount = 10_000,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["ARKADE"] }
        }, cancellationToken);
        var prompt = await GetPaymentPromptAsync(merchant, invoice.Id, "ARKADE", cancellationToken);
        var route = await GetRouteAsync(greenfield, store.Id, invoice.Id, cancellationToken);
        AssertCompositionQuote(route, "ARKADE", prompt.Destination!);
        infrastructure.AssertPublicProjectionSafe(route.GetRawText());
        Assert.Equal(InvoiceStatus.New, (await merchant.GetInvoice(invoice.Id, token: cancellationToken)).Status);

        await infrastructure.SendArkadePaymentAsync(prompt.Destination!, 10_000, cancellationToken);

        var settledRoute = await WaitForVerifiedSettlementAsync(merchant, greenfield, store.Id, invoice.Id, cancellationToken);
        await AssertEvmDeliveryAndInvoiceSettlementAsync(merchant, invoice.Id, settledRoute, before, cancellationToken);
    }

    [Fact]
    public async Task WatchOnlyMerchant_LightningInvoice_RestartsBtCPayThenSettlesAfterExactErc20Receipt()
    {
        Assert.SkipWhen(!infrastructure.IsEnabled,
            $"Set {ComposedEvmInfrastructureFixture.EnableVariable}=1 and provide the regtest root and secrets file.");
        var cancellationToken = TestContext.Current.CancellationToken;
        await infrastructure.StartAsync(cancellationToken);
        btcpay.Initialize(this);
        await infrastructure.AssertBtCPayReadinessAsync(btcpay.ServerTester!.PayTester.HttpClient, cancellationToken);
        await InitializePlaywright(btcpay.ServerTester);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var store = await client.CreateStore(new CreateStoreRequest { Name = "Composed EVM Lightning merchant" }, cancellationToken);
        var merchant = await CreateStoreScopedClientAsync(client, store.Id, cancellationToken);
        using var greenfield = CreateGreenfieldClient(merchant);
        await SetupWatchOnlyWalletAsync(greenfield, store.Id, enableLightning: true, cancellationToken);
        await infrastructure.ConfigureMerchantSettlementAsync(greenfield, store.Id, ["BTC-LN"], cancellationToken);

        var before = await infrastructure.GetErc20BalanceAsync(infrastructure.MerchantEvmAddress, cancellationToken);
        var invoice = await merchant.CreateInvoice(store.Id, new CreateInvoiceRequest
        {
            Amount = 10_000,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["BTC-LN"] }
        }, cancellationToken);
        var prompt = await GetPaymentPromptAsync(merchant, invoice.Id, "BTC-LN", cancellationToken);
        var route = await GetRouteAsync(greenfield, store.Id, invoice.Id, cancellationToken);
        AssertCompositionQuote(route, "BTC-LN", prompt.Destination!);
        infrastructure.AssertPublicProjectionSafe(route.GetRawText());
        Assert.Equal(InvoiceStatus.New, (await merchant.GetInvoice(invoice.Id, token: cancellationToken)).Status);

        Task? lightningPayment = null;
        using var lightningPaymentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await btcpay.RestartAsync(async stopped =>
            {
                lightningPayment = infrastructure.PayLightningInvoiceAsync(
                    prompt.Destination!, lightningPaymentCancellation.Token);
                await infrastructure.WaitForLightningInvoiceAcceptedAsync(
                    prompt.Destination!, lightningPayment, stopped);
            }, cancellationToken);
            await (lightningPayment ?? throw new InvalidOperationException("The Lightning payment was not started."));
        }
        catch
        {
            lightningPaymentCancellation.Cancel();
            await infrastructure.PreserveDiagnosticsAsync(route.GetRawText());
            throw;
        }
        await infrastructure.AssertBtCPayReadinessAsync(btcpay.ServerTester!.PayTester.HttpClient, cancellationToken);
        var settledRoute = await WaitForVerifiedSettlementAsync(merchant, greenfield, store.Id, invoice.Id, cancellationToken);
        await AssertEvmDeliveryAndInvoiceSettlementAsync(merchant, invoice.Id, settledRoute, before, cancellationToken);
    }

    [Fact]
    public async Task WatchOnlyMerchant_OnchainInvoice_ComposesIngressThenSettlesAfterExactErc20Receipt()
    {
        Assert.SkipWhen(!infrastructure.IsEnabled,
            $"Set {ComposedEvmInfrastructureFixture.EnableVariable}=1 and provide the regtest root and secrets file.");
        var cancellationToken = TestContext.Current.CancellationToken;
        await infrastructure.StartAsync(cancellationToken);
        btcpay.Initialize(this);
        await infrastructure.AssertBtCPayReadinessAsync(btcpay.ServerTester!.PayTester.HttpClient, cancellationToken);
        await InitializePlaywright(btcpay.ServerTester);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var store = await client.CreateStore(new CreateStoreRequest { Name = "Composed EVM onchain merchant" }, cancellationToken);
        var merchant = await CreateStoreScopedClientAsync(client, store.Id, cancellationToken);
        using var greenfield = CreateGreenfieldClient(merchant);
        await SetupWatchOnlyWalletAsync(greenfield, store.Id, enableLightning: false, cancellationToken);
        await merchant.UpdateStorePaymentMethod(store.Id, "BTC-CHAIN", new UpdatePaymentMethodRequest
        {
            Enabled = true,
            Config = JValue.CreateString(WatchOnlyOnchainXpub())
        }, cancellationToken);
        await infrastructure.ConfigureMerchantSettlementAsync(greenfield, store.Id, ["BTC-CHAIN"], cancellationToken);

        var before = await infrastructure.GetErc20BalanceAsync(infrastructure.MerchantEvmAddress, cancellationToken);
        var invoice = await merchant.CreateInvoice(store.Id, new CreateInvoiceRequest
        {
            Amount = 10_000,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["BTC-CHAIN"] }
        }, cancellationToken);
        var prompt = await GetPaymentPromptAsync(merchant, invoice.Id, "BTC-CHAIN", cancellationToken);
        var route = await GetRouteAsync(greenfield, store.Id, invoice.Id, cancellationToken);
        AssertCompositionQuote(route, "BTC-CHAIN", prompt.Destination!);
        infrastructure.AssertPublicProjectionSafe(route.GetRawText());
        Assert.Equal(InvoiceStatus.New, (await merchant.GetInvoice(invoice.Id, token: cancellationToken)).Status);

        var due = route.GetProperty("baseAmountSats").GetInt64() + route.GetProperty("ingressFeeSats").GetInt64();
        await infrastructure.SendOnchainPaymentAsync(prompt.Destination!, due, cancellationToken);
        var settledRoute = await WaitForVerifiedSettlementAsync(merchant, greenfield, store.Id, invoice.Id, cancellationToken);
        await AssertEvmDeliveryAndInvoiceSettlementAsync(merchant, invoice.Id, settledRoute, before, cancellationToken);
    }

    private async Task<BTCPayServerClient> CreateStoreScopedClientAsync(BTCPayServerClient owner, string storeId,
        CancellationToken cancellationToken)
    {
        var apiKey = await owner.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "composed-evm-e2e",
            Permissions =
            [
                Permission.Create(Policies.CanModifyStoreSettings, storeId),
                Permission.Create(Policies.CanViewStoreSettings, storeId),
                Permission.Create(Policies.CanViewInvoices, storeId),
                Permission.Create(Policies.CanCreateInvoice, storeId)
            ]
        }, cancellationToken);
        return new BTCPayServerClient(ServerUri, apiKey.ApiKey);
    }

    private HttpClient CreateGreenfieldClient(BTCPayServerClient owner)
    {
        var client = new HttpClient { BaseAddress = ServerUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", owner.APIKey);
        return client;
    }

    private static async Task SetupWatchOnlyWalletAsync(HttpClient client, string storeId, bool enableLightning,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"api/v1/stores/{storeId}/arkade/wallet",
            new StringContent(JsonSerializer.Serialize(new
            {
                wallet = WatchOnlyDescriptor,
                mode = "WatchOnly",
                enableLightning
            }), Encoding.UTF8, "application/json"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Greenfield watch-only wallet setup returned HTTP {(int)response.StatusCode} at {response.RequestMessage?.RequestUri}: {detail[..Math.Min(detail.Length, 2_000)]}");
        }
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return body.RootElement.Clone();
    }

    private static async Task<InvoicePaymentMethodDataModel> GetPaymentPromptAsync(BTCPayServerClient client, string invoiceId,
        string paymentMethodId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var method = (await client.GetInvoicePaymentMethods(invoiceId, token: cancellationToken))
                .SingleOrDefault(candidate => candidate.PaymentMethodId == paymentMethodId);
            if (method?.Destination is { Length: > 0 }) return method;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException($"BTCPay did not generate the composed {paymentMethodId} payment method.");
    }

    private static async Task<JsonElement> GetRouteAsync(HttpClient client, string storeId, string invoiceId,
        CancellationToken cancellationToken)
    {
        var routes = await GetJsonAsync(client, $"api/v1/stores/{storeId}/arkade/evm-settlement/routes?invoiceId={invoiceId}",
            cancellationToken);
        return routes.EnumerateArray().Single().Clone();
    }

    private static void AssertCompositionQuote(JsonElement route, string rail, string customerDestination)
    {
        Assert.Equal(rail, route.GetProperty("paymentMethodId").GetString());
        Assert.Equal(rail == "ARKADE" ? "OutgoingQuoted" : "IngressQuoted", route.GetProperty("status").GetString());
        var paymentHash = route.GetProperty("paymentHash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paymentHash));
        var legs = route.GetProperty("legs").EnumerateArray().ToArray();
        Assert.Equal(rail == "ARKADE" ? 1 : 2, legs.Length);
        var outgoing = Assert.Single(legs, leg => leg.GetProperty("kind").GetString() == "Outgoing");
        var outgoingQuote = outgoing.GetProperty("quote");
        Assert.Equal(paymentHash, outgoingQuote.GetProperty("paymentHash").GetString());
        Assert.Equal(JsonValueKind.Null, outgoingQuote.GetProperty("payoutScript").ValueKind);
        if (rail == "ARKADE")
        {
            Assert.Equal(outgoingQuote.GetProperty("lockupAddress").GetString(), customerDestination);
            return;
        }

        var ingress = Assert.Single(legs, leg => leg.GetProperty("kind").GetString() == "Ingress");
        var ingressQuote = ingress.GetProperty("quote");
        Assert.Equal(paymentHash, ingressQuote.GetProperty("paymentHash").GetString());
        Assert.Equal(outgoingQuote.GetProperty("lockupScript").GetString(), ingressQuote.GetProperty("payoutScript").GetString());
        Assert.Equal(customerDestination, route.GetProperty("customerDestination").GetString());
    }

    private async Task<JsonElement> WaitForVerifiedSettlementAsync(BTCPayServerClient client, HttpClient greenfield,
        string storeId, string invoiceId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var lastStatus = "unavailable";
        string? lastRoute = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var route = await GetRouteAsync(greenfield, storeId, invoiceId, cancellationToken);
            infrastructure.AssertPublicProjectionSafe(route.GetRawText());
            lastRoute = route.GetRawText();
            lastStatus = route.GetProperty("status").GetString() ?? "missing";
            if (route.TryGetProperty("failureCode", out var failure) && failure.ValueKind == JsonValueKind.String &&
                failure.GetString() != "RemoteUnavailable")
            {
                await infrastructure.PreserveDiagnosticsAsync(lastRoute);
                throw new InvalidOperationException($"The composed route failed with public code {failure.GetString()}. EVM send solver admin: {infrastructure.RuntimeSolverAdminPath}");
            }
            var invoice = await client.GetInvoice(invoiceId, token: cancellationToken);
            if (route.GetProperty("status").GetString() == "EvmClaimVerified") return route;
            Assert.NotEqual(InvoiceStatus.Settled, invoice.Status);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        await infrastructure.PreserveDiagnosticsAsync(lastRoute);
        throw new TimeoutException($"The composed route never reached verified EVM delivery; last public status was {lastStatus}. Runtime logs: {infrastructure.RuntimeLogPath}; route: {infrastructure.RuntimeRoutePath}; EVM send solver admin: {infrastructure.RuntimeSolverAdminPath}");
    }

    private async Task AssertEvmDeliveryAndInvoiceSettlementAsync(BTCPayServerClient client, string invoiceId,
        JsonElement settledRoute, BigInteger before, CancellationToken cancellationToken)
    {
        var amount = BigInteger.Parse(settledRoute.GetProperty("evmTerms").GetProperty("amount").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var receipt = settledRoute.GetProperty("evmClaimTransactionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(receipt));
        await infrastructure.AssertExactErc20ReceiptAsync(receipt!, infrastructure.MerchantEvmAddress, amount, cancellationToken);
        Assert.Equal(before + amount,
            await infrastructure.GetErc20BalanceAsync(infrastructure.MerchantEvmAddress, cancellationToken));
        Assert.Equal(InvoiceStatus.Settled, (await WaitForSettledInvoiceAsync(client, invoiceId, cancellationToken)).Status);
    }

    private static string WatchOnlyOnchainXpub() => new Mnemonic("all all all all all all all all all all all all")
        .DeriveExtKey().Derive(KeyPath.Parse("m/84'/1'/0'")).Neuter().ToString(Network.RegTest);

    private static async Task<InvoiceData> WaitForSettledInvoiceAsync(BTCPayServerClient client, string invoiceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var invoice = await client.GetInvoice(invoiceId, token: cancellationToken);
            if (invoice.Status == InvoiceStatus.Settled) return invoice;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException("BTCPay did not settle the invoice after the verified ERC20 receipt.");
    }
}
