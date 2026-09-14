using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Reflection;
using System.Text.Encodings.Web;
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Controllers;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace NArk.Tests;

public partial class ArkEvmSettlementApiTests
{
    private const string Asset = "eip155:42161/erc20:0x1111111111111111111111111111111111111111";
    private const string Destination = "0x2222222222222222222222222222222222222222";

    [Theory]
    [InlineData("GET", "", null, HttpStatusCode.Unauthorized)]
    [InlineData("PUT", "", null, HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/capabilities", null, HttpStatusCode.Unauthorized)]
    [InlineData("GET", "", "unrelated", HttpStatusCode.Forbidden)]
    [InlineData("GET", "/capabilities", "unrelated", HttpStatusCode.Forbidden)]
    [InlineData("PUT", "", Policies.CanViewStoreSettings, HttpStatusCode.Forbidden)]
    public async Task EndpointRequiresGreenfieldStorePermission(
        string method, string suffix, string? permission, HttpStatusCode expected)
    {
        await using var app = CreateHost();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/stores/store/arkade/evm-settlement{suffix}");
        if (permission is not null) request.Headers.Add("Test-Permission", permission);

        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task WatchOnlyStoreCanSaveConfigurationWithoutEnablingExecution()
    {
        var configuration = new ArkadePaymentMethodConfig("watch-only-wallet", BoardingEnabled: false);
        await using var app = CreateHost(configuration);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanModifyStoreSettings);
        var input = CompleteConfiguration();
        input["preimage"] = "secret-must-not-survive";
        using var response = await PutConfiguration(client, input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("secret-must-not-survive", await response.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);
        var stored = JObject.Parse(await client.GetStringAsync("/api/v1/stores/store/arkade/evm-settlement"));
        Assert.Equal(Asset, stored.Value<string>("assetId"));
        Assert.Equal(Destination, stored.Value<string>("destination"));
        Assert.True(stored.Value<bool>("enabled"));
        var capabilities = JObject.Parse(await client.GetStringAsync("/api/v1/stores/store/arkade/evm-settlement/capabilities"));
        Assert.True(capabilities.Value<bool>("walletConfigured"));
        Assert.False(capabilities.Value<bool>("signerAvailable"));
        Assert.False(capabilities.Value<bool>("executionAvailable"));
        Assert.True(capabilities.Value<bool>("configurationEnabled"));
        Assert.Equal("sdk-composition-unavailable", capabilities.Value<string>("blockedReason"));
        Assert.Equal("evm-settlement", capabilities.Value<string>("paymentCompletionCondition"));
        var store = app.Services.GetRequiredService<StoreData>();
        var persisted = app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!;
        Assert.Equal(configuration.WalletId, persisted.WalletId);
        Assert.False(persisted.BoardingEnabled);
    }

    [Fact]
    public async Task SqliteExecutionLockBlocksRouteIssuanceAndReportsCapability()
    {
        await using var database = await RouteDatabase.Create();
        var repository = new ArkCompositionRouteRepository(database);
        var intents = TestProxy.Create<IArkadeIntentStorage>((method, _) => method.Name switch
        {
            nameof(IArkadeIntentStorage.GetArkadeSwapIntents) =>
                Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>([]),
            _ => throw new NotSupportedException(method.Name)
        });
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet"), repository: repository,
            prompts: new ArkCompositionPromptService(repository, intents),
            executionLock: new ArkCompositionPostgresExecutionLock(database));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanModifyStoreSettings);
        using (var configured = await PutConfiguration(client, CompleteConfiguration()))
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);

        var capabilities = JObject.Parse(await client.GetStringAsync("/api/v1/stores/store/arkade/evm-settlement/capabilities"));

        Assert.True(capabilities.Value<bool>("sdkCompositionAvailable"));
        Assert.False(capabilities.Value<bool>("executionAvailable"));
        Assert.Contains("cross-process-execution-lock-unavailable", capabilities["missingConfiguration"]!.Values<string>());
    }

    [Fact]
    public async Task RouteReportsIntentStateWithoutTreatingIngressAsSettlement()
    {
        await using var database = await RouteDatabase.Create();
        var repository = new ArkCompositionRouteRepository(database);
        var paymentHash = new string('c', 64);
        var outgoingId = new string('a', 64);
        var ingressId = new string('b', 64);
        await repository.Add("store", ArkCompositionRoute.Create("store", null, "BTC-LN", "wallet",
            outgoingId, ingressId, paymentHash, 1000, "lnbcrt1testpublicquote", 1800000030,
            DateTimeOffset.FromUnixTimeSeconds(1800000000)));
        var intents = TestProxy.Create<IArkadeIntentStorage>((method, args) => method.Name switch
        {
            nameof(IArkadeIntentStorage.GetArkadeSwapIntents) => Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(
                IntentById((string?)args![0]!)),
            _ => throw new NotSupportedException(method.Name)
        });
        await using var app = CreateHost(repository: repository, intents: intents,
            executionLock: new SafeExecutionLock());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);

        var stored = (await repository.List("store")).Single();
        var data = JObject.Parse(await client.GetStringAsync($"/api/v1/stores/store/arkade/evm-settlement/routes/{stored.RouteId}"));

        Assert.Equal("Pending", data.Value<string>("status"));
        Assert.False(data.Value<bool>("settlementVerified"));
        Assert.True(data.Value<bool>("executionAvailable"));
        Assert.Equal(2, data["legs"]!.Count());
        Assert.Equal(paymentHash, data.Value<string>("paymentHash"));

        IReadOnlyCollection<ArkadeSwapIntent> IntentById(string? id) => id switch
        {
            _ when id == outgoingId => [new ArkadeSwapIntent
            {
                Id = outgoingId, WalletId = "wallet", Type = ArkadeSwapIntentType.BtcToEvm,
                OfferAmount = Money.Satoshis(1000), WantAmount = Money.Zero, Status = ArkadeSwapIntentStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow, SwapPkScript = "0014", SwapAddress = "ark1",
                PaymentHash = paymentHash, RefundLocktime = 1800000060,
                ToAssetId = "eip155:42161/erc20:0x1111111111111111111111111111111111111111"
            }.WithEvmMetadata(new EvmSwapMetadata(new string('1', 64), "2000000",
                "0x1111111111111111111111111111111111111111", Destination,
                "0x3333333333333333333333333333333333333333", "1900000",
                "0x4444444444444444444444444444444444444444")).WithSolver(new string('7', 64))],
            _ when id == ingressId => [new ArkadeSwapIntent
            {
                Id = ingressId, WalletId = "wallet", Type = ArkadeSwapIntentType.LightningToBtc,
                OfferAmount = Money.Satoshis(1025), WantAmount = Money.Satoshis(1000),
                Status = ArkadeSwapIntentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                SwapPkScript = "0020", SwapAddress = "ark2", PaymentHash = paymentHash, RefundLocktime = 1800000060
            }.WithSolver(new string('8', 64))],
            _ => []
        };
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("GET", "/capabilities")]
    public async Task AuthenticatedStoreCannotBeReplacedByRouteStore(string method, string suffix)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/stores/other/arkade/evm-settlement{suffix}");
        request.Headers.Add("Test-Permission", method == "PUT" ? Policies.CanModifyStoreSettings : Policies.CanViewStoreSettings);
        if (method == "PUT") request.Content = JsonContent.Create(new { assetId = Asset, destination = Destination });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("private-wallet", await response.Content.ReadAsStringAsync());
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Fact]
    public async Task InvalidConfigurationIsRejectedWithoutChangingStoreOrEchoingInput()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanModifyStoreSettings);
        using var response = await HttpClientJsonExtensions.PutAsJsonAsync(client, "/api/v1/stores/store/arkade/evm-settlement",
            new { assetId = Asset, destination = "nsec-sensitive" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("nsec-sensitive", await response.Content.ReadAsStringAsync());
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Fact]
    public async Task StoreWithoutArkadeWalletCannotEnableSettlement()
    {
        await using var app = CreateHost();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanModifyStoreSettings);
        using var response = await HttpClientJsonExtensions.PutAsJsonAsync(client, "/api/v1/stores/store/arkade/evm-settlement",
            new { assetId = Asset, destination = Destination, enabled = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static WebApplication CreateHost(ArkadePaymentMethodConfig? initialConfiguration = null, bool newtonsoft = true,
        TestLogSink? logSink = null, ArkCompositionRouteRepository? repository = null,
        ArkCompositionPromptService? prompts = null, bool onchainConfigured = true,
        IArkadeIntentStorage? intents = null, IArkCompositionExecutionLock? executionLock = null,
        bool lightningConfigured = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<ArkEvmRpcEndpointProtector>();
        builder.Services.AddSingleton<ArkEvmGasPayerProtector>();
        if (repository is not null) builder.Services.AddSingleton(repository);
        if (prompts is not null) builder.Services.AddSingleton(prompts);
        if (intents is not null) builder.Services.AddSingleton(intents);
        if (executionLock is not null) builder.Services.AddSingleton(executionLock);
        builder.Logging.ClearProviders();
        if (logSink is not null)
        {
            builder.Logging.AddProvider(logSink);
            builder.Logging.AddFilter<TestLogSink>(null, LogLevel.Trace);
        }
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var mvc = builder.Services.AddControllers(options => options.Filters.Add<InjectedModelState>())
            .ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Clear();
            parts.ApplicationParts.Add(new SettlementPart());
        });
        if (newtonsoft) mvc.AddNewtonsoftJson();
        builder.Services.AddCors(options => options.AddPolicy(CorsPolicies.All, policy => policy.AllowAnyOrigin()));
        var schemes = AuthenticationSchemes.Greenfield.Split(',');
        var authentication = builder.Services.AddAuthentication(schemes[0]);
        foreach (var scheme in schemes)
            authentication.AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var policy in new[] { Policies.CanViewStoreSettings, Policies.CanModifyStoreSettings, Policies.CanViewInvoices, Policies.CanCreateInvoice })
                options.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireClaim("permission", policy));
        });
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var store = new StoreData { Id = "store" };
        if (initialConfiguration is not null) store.SetPaymentMethodConfig(handler, initialConfiguration);
        if (onchainConfigured)
            store.SetPaymentMethodConfig(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"), new JObject());
        if (lightningConfigured)
            store.SetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC"), JObject.FromObject(
                new BTCPayServer.Payments.Lightning.LightningPaymentMethodConfig
                {
                    ConnectionString = BTCPayServer.Plugins.ArkPayServer.Lightning.ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(
                        initialConfiguration?.WalletId ?? "wallet", store.Id)
                }));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TestProxy.Create<IArkEvmSettlementStore>((method, args) => method.Name switch
        {
            nameof(IArkEvmSettlementStore.GetConfiguration) =>
                ((StoreData)args![0]!).GetPaymentMethodConfig<ArkadePaymentMethodConfig>(handler.PaymentMethodId, handlers),
            nameof(IArkEvmSettlementStore.SaveAsync) => Save((StoreData)args![0]!, (ArkadePaymentMethodConfig)args[1]!),
            _ => throw new NotSupportedException(method.Name)
        }));
        Task Save(StoreData owner, ArkadePaymentMethodConfig configuration)
        {
            owner.SetPaymentMethodConfig(handler, configuration);
            return Task.CompletedTask;
        }
        builder.Services.AddSingleton(TestProxy.Create<IWalletProvider>((method, _) => method.Name switch
        {
            nameof(IWalletProvider.GetSignerAsync) => Task.FromResult<IArkadeWalletSigner?>(null),
            _ => throw new NotSupportedException(method.Name)
        }));
        var app = builder.Build();
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) context.SetStoreData(store);
            await next();
        });
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Test-Permission", out var permission))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("permission", permission.ToString())], AuthenticationSchemes.Greenfield));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class SettlementPart : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => nameof(SettlementPart);
        public IEnumerable<TypeInfo> Types => [typeof(ArkEvmSettlementController).GetTypeInfo(), typeof(ArkCompositionRoutesController).GetTypeInfo(), typeof(ArkCompositionPromptsController).GetTypeInfo()];
    }

    private sealed class SafeExecutionLock : IArkCompositionExecutionLock
    {
        public bool SupportsCrossProcessExecution => true;
        public ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
