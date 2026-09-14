using System.Net;
using System.Text;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using System.Reflection;
using BTCPayServer.Plugins.ArkPayServer.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public partial class ArkEvmSettlementApiTests
{
    private const string Endpoint = "/api/v1/stores/store/arkade/evm-settlement";
    private const string PublicKey = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    [Fact]
    public async Task SettlementGetRetainsNormalModelStateValidation()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);
        client.DefaultRequestHeaders.Add("Test-Invalid-Model-State", "true");

        using var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unrelated action validation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SettlementPutRemainsAJsonBodyOperationInApiMetadata()
    {
        await using var app = CreateHost();
        var explorer = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var operation = Assert.Single(explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items),
            d => d.HttpMethod == "PUT" && d.RelativePath!.EndsWith("arkade/evm-settlement"));

        var body = Assert.Single(operation.ParameterDescriptions, p => p.Source == BindingSource.Body);
        Assert.Equal(typeof(ArkEvmSettlementUpdateData), body.Type);
    }

    [Fact]
    public async Task RequestAbortCancelsTheBoundedBodyRead()
    {
        await using var app = CreateHost();
        var parameter = typeof(ArkEvmSettlementController).GetMethod(nameof(ArkEvmSettlementController.SetConfiguration))!
            .GetParameters().Single(p => p.Name == "update");
        var binderType = parameter.GetCustomAttribute<ModelBinderAttribute>()!.BinderType!;
        var binder = (IModelBinder)ActivatorUtilities.CreateInstance(app.Services, binderType);
        using var cancellation = new CancellationTokenSource();
        await using var body = new AbortingBody(cancellation);
        var http = new DefaultHttpContext { RequestAborted = cancellation.Token };
        http.Request.ContentType = "application/json";
        http.Request.Body = body;
        var metadata = app.Services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(ArkEvmSettlementUpdateData));
        var context = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            new CompositeValueProvider(), metadata, null, "update");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binder.BindModelAsync(context));

        Assert.True(body.ReadStarted);
        Assert.False(context.Result.IsModelSet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationRequestsDoNotWriteSecretsToLogs(bool malformed)
    {
        using var logs = new TestLogSink();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), logSink: logs);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        if (malformed) input["routePolicy"]!["minConfirmations"] = "secret-invalid-number";

        using var response = await PutConfiguration(client, input);

        Assert.Equal(malformed ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, line => Assert.DoesNotContain("secret-", line));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("object")]
    [InlineData("depth")]
    [InlineData("content-type")]
    [InlineData("chunked-limit")]
    public async Task UntrustedJsonIsBoundedAndRejectedWithoutLoggingBody(string failure)
    {
        using var logs = new TestLogSink();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), logSink: logs);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        if (failure == "object") input["rpcEndpoint"] = "secret-invalid-object";
        if (failure == "depth")
        {
            JToken nested = "secret-deep-value";
            for (var i = 0; i < 40; i++) nested = new JObject { ["nested"] = nested };
            input["extension"] = nested;
        }
        if (failure == "chunked-limit") input["extension"] = "secret-oversized" + new string('x', 32768);
        var body = failure == "malformed" ? "{\"assetId\":\"secret-invalid-json\",\"enabled\":" : input.ToString();
        using HttpContent content = failure == "chunked-limit" ? new ChunkedJsonContent(body) :
            new StringContent(body, Encoding.UTF8, failure == "content-type" ? "text/plain" : "application/json");
        if (failure == "chunked-limit")
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            Assert.Null(content.Headers.ContentLength);
        }

        using var response = await client.PutAsync(Endpoint, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-", await response.Content.ReadAsStringAsync());
        Assert.All(logs.Messages, line => Assert.DoesNotContain("secret-", line));
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletePolicyRoundTripsWithoutExposingRpcCredentialsOrEnablingExecution(bool newtonsoft)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), newtonsoft);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input["protectedRpcUri"] = "caller-controlled-ciphertext";

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var putBody = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(putBody);
        Assert.Equal("https://rpc.example", data.Value<string>("rpcEndpointOrigin"));
        Assert.True(data.Value<bool>("rpcEndpointConfigured"));
        Assert.True(data.Value<bool>("configurationComplete"));
        Assert.Equal(new[] { "ARKADE", "BTC-LN", "BTC-CHAIN" }, data["routePolicy"]!["enabledSourceRails"]!.Values<string>());
        Assert.Equal(0.25m, data["routePolicy"]!.Value<decimal>("fastestSecondsPerBlock"));
        Assert.Equal("https://solver.example/proxy/", data["routePolicy"]!["outgoingSolver"]!.Value<string>("endpoint"));
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);
        var getBody = await client.GetStringAsync(Endpoint);
        var capabilities = await client.GetStringAsync(Endpoint + "/capabilities");
        var capabilityData = JObject.Parse(capabilities);
        Assert.True(capabilityData.Value<bool>("configurationComplete"));
        Assert.False(capabilityData.Value<bool>("executionAvailable"));
        Assert.False(capabilityData.Value<bool>("signerAvailable"));
        Assert.Equal("evm-settlement", capabilityData.Value<string>("paymentCompletionCondition"));
        Assert.Equal(data, JObject.Parse(getBody));
        var store = app.Services.GetRequiredService<StoreData>();
        var persisted = app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!;
        var storedJson = store.GetPaymentMethodConfigs().ToString();
        foreach (var output in new[] { putBody, getBody, capabilities, storedJson, persisted.ToString() })
        {
            Assert.DoesNotContain("secret-", output);
            Assert.DoesNotContain("caller-controlled-ciphertext", output);
        }
        Assert.DoesNotContain("protectedRpcUri", putBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(persisted.EvmSettlement!.ProtectedRpcUri!, putBody);
        var rpc = app.Services.GetRequiredService<ArkEvmRpcEndpointProtector>()
            .TryUnprotect(store.Id, persisted.EvmSettlement.ProtectedRpcUri);
        Assert.Equal("https://secret-user:secret-pass@rpc.example/secret-path?key=secret-query", rpc!.AbsoluteUri);
    }

    [Fact]
    public async Task RpcUpdateRequiresExplicitReplaceAndCanPreserveOrClearWithoutEchoingUri()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        using var created = await PutConfiguration(client, CompleteConfiguration());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var store = app.Services.GetRequiredService<StoreData>();
        var saved = store.GetPaymentMethodConfigs().ToString();
        var preserve = CompleteConfiguration();
        preserve["rpcEndpoint"] = JObject.Parse("{\"action\":\"preserve\"}");
        preserve["gasPayerPrivateKey"] = JObject.Parse("{\"action\":\"preserve\"}");

        using var preserved = await PutConfiguration(client, preserve);

        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.Equal(saved, store.GetPaymentMethodConfigs().ToString());
        preserve["rpcEndpoint"] = JObject.Parse("{\"action\":\"clear\"}");
        using var rejected = await PutConfiguration(client, preserve);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(saved, store.GetPaymentMethodConfigs().ToString());
        preserve["enabled"] = false;
        using var cleared = await PutConfiguration(client, preserve);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var result = JObject.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.False(result.Value<bool>("rpcEndpointConfigured"));
        Assert.False(result.Value<bool>("configurationComplete"));
        Assert.Contains("rpc-endpoint-missing", result["missingConfiguration"]!.Values<string>());
    }

    [Theory]
    [InlineData("routePolicy.minConfirmations", "\"secret-invalid-number\"")]
    [InlineData("routePolicy.enabledSourceRails", "\"secret-invalid-array\"")]
    [InlineData("routePolicy.fastestSecondsPerBlock", "\"secret-invalid-decimal\"")]
    [InlineData("routePolicy.requireEmulatorRefundPath", "\"secret-invalid-boolean\"")]
    public async Task ModelBindingErrorsDoNotEchoSubmittedSecrets(string path, string value)
    {
        using var logs = new TestLogSink();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), logSink: logs);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input.SelectToken(path)!.Replace(JToken.Parse(value));

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-", await response.Content.ReadAsStringAsync());
        Assert.All(logs.Messages, line => Assert.DoesNotContain("secret-", line));
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Theory]
    [InlineData("registry", null)]
    [InlineData("explicit", "wss://relay.example/channel")]
    [InlineData("explicit", "http://localhost:9000/solver")]
    public async Task SolverIdentityPinsRoundTripUsingTheSelectedTransport(string mode, string? endpoint)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        var pinName = mode == "registry" ? "discoveryPubkey" : "solverPubkey";
        input["routePolicy"]!["outgoingSolver"] = new JObject
        {
            ["mode"] = mode,
            ["endpoint"] = endpoint,
            [pinName] = PublicKey.ToUpperInvariant()
        };

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PublicKey, data["routePolicy"]!["outgoingSolver"]!.Value<string>(pinName));
    }

    [Fact]
    public void ProtectedRpcCannotBeMovedAcrossStoresOrReadWithoutTheDeploymentKey()
    {
        var protector = new ArkEvmRpcEndpointProtector(new EphemeralDataProtectionProvider());
        var ciphertext = protector.Protect("store", "https://secret-user:secret-pass@rpc.example/secret-path?secret-query");

        Assert.NotNull(protector.TryUnprotect("store", ciphertext));
        Assert.Null(protector.TryUnprotect("other-store", ciphertext));
        Assert.Null(protector.TryUnprotect("store", "invalid-ciphertext"));
        Assert.Null(new ArkEvmRpcEndpointProtector(new EphemeralDataProtectionProvider()).TryUnprotect("store", ciphertext));
        var input = JsonConvert.DeserializeObject<ArkEvmSettlementUpdateData>(CompleteConfiguration().ToString())!;
        Assert.DoesNotContain("secret-", input.ToString());
    }

    [Theory]
    [InlineData("registry", "discoveryPubkey", '0')]
    [InlineData("registry", "discoveryPubkey", 'f')]
    [InlineData("explicit", "solverPubkey", '0')]
    [InlineData("explicit", "solverPubkey", 'f')]
    public async Task SolverPinsMustBeActualSecp256k1XOnlyPoints(string mode, string keyName, char digit)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input["routePolicy"]!["outgoingSolver"] = new JObject
        {
            ["mode"] = mode,
            ["endpoint"] = mode == "explicit" ? "https://solver.example" : null,
            [keyName] = new string(digit, 64)
        };

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RemovingWalletMakesAnOtherwiseCompleteConfigurationIncomplete()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        using var saved = await PutConfiguration(client, CompleteConfiguration());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var store = app.Services.GetRequiredService<StoreData>();
        var repository = app.Services.GetRequiredService<IArkEvmSettlementStore>();
        await repository.SaveAsync(store, repository.GetConfiguration(store)! with { WalletId = "" });
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);

        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));
        var data = JObject.Parse(await client.GetStringAsync(Endpoint));

        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.False(data.Value<bool>("configurationComplete"));
        Assert.False(capabilities.Value<bool>("executionAvailable"));
        Assert.Contains("arkade-wallet-missing", capabilities["missingConfiguration"]!.Values<string>());
        Assert.Contains("arkade-wallet-missing", data["missingConfiguration"]!.Values<string>());
    }

    [Fact]
    public async Task AbsentWalletAndSettingsHaveIndependentMissingConfigurationFacts()
    {
        await using var app = CreateHost();
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);

        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));

        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.Contains("arkade-wallet-missing", capabilities["missingConfiguration"]!.Values<string>());
        Assert.Contains("settlement-settings-missing", capabilities["missingConfiguration"]!.Values<string>());
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("excluded", false)]
    [InlineData("enabled", true)]
    public async Task ArkadeCompositionRequiresAnEnabledPaymentMethod(string configuration, bool expectedReady)
    {
        var initial = configuration == "absent" ? null : new ArkadePaymentMethodConfig("wallet");
        await using var app = CreateHost(initial);
        if (configuration == "excluded")
            app.Services.GetRequiredService<StoreData>().StoreBlob =
                "{\"excludedPaymentMethods\":[\"ARKADE\"]}";
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());

        if (configuration != "absent")
        {
            using var response = await PutConfiguration(client, CompleteConfiguration());
            Assert.Equal(expectedReady ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            if (!expectedReady)
            {
                var disabled = CompleteConfiguration();
                disabled["enabled"] = false;
                using var saved = await PutConfiguration(client, disabled);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }
        }

        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);
        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));
        Assert.Equal(expectedReady, capabilities.Value<bool>("configurationComplete"));
        if (!expectedReady)
            Assert.Contains("arkade-payment-method-missing", capabilities["missingConfiguration"]!.Values<string>());
    }

    [Fact]
    public async Task OnchainCompositionReportsMissingCorePaymentMethod()
    {
        var configuration = new ArkadePaymentMethodConfig("wallet")
        {
            EvmSettlement = new ArkEvmSettlementSettings(Asset, Destination)
            {
                RoutePolicy = CompleteConfiguration()["routePolicy"]!.ToObject<ArkEvmRoutePolicy>()
            }
        };
        await using var app = CreateHost(configuration, onchainConfigured: false);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);

        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));

        Assert.Contains("onchain-payment-method-missing", capabilities["missingConfiguration"]!.Values<string>());
        Assert.False(capabilities.Value<bool>("configurationComplete"));
    }

    [Theory]
    [InlineData("excluded", false)]
    [InlineData("enabled", true)]
    public async Task OnchainCompositionRequiresAnEnabledCorePaymentMethod(string configuration, bool expectedReady)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        if (configuration == "excluded")
            app.Services.GetRequiredService<StoreData>().StoreBlob =
                "{\"excludedPaymentMethods\":[\"BTC-CHAIN\"]}";
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input["routePolicy"]!["enabledSourceRails"] = new JArray("BTC-CHAIN");
        input["routePolicy"]!["lightningIngressSolver"] = null;

        using var response = await PutConfiguration(client, input);

        Assert.Equal(expectedReady ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (expectedReady) return;
        input["enabled"] = false;
        using var saved = await PutConfiguration(client, input);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);
        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));
        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.Contains("onchain-payment-method-missing", capabilities["missingConfiguration"]!.Values<string>());
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("excluded", false)]
    [InlineData("wrong-wallet", false)]
    [InlineData("wrong-store", false)]
    [InlineData("unscoped-store", false)]
    [InlineData("exact", true)]
    public async Task LightningCompositionRequiresAnEnabledExactArkadePaymentMethod(string configuration, bool expectedReady)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), lightningConfigured: false);
        var store = app.Services.GetRequiredService<StoreData>();
        var lightning = BTCPayServer.Payments.PaymentTypes.LN.GetPaymentMethodId("BTC");
        var connectionString = configuration switch
        {
            "wrong-wallet" => BTCPayServer.Plugins.ArkPayServer.Lightning.ArkLightningSpendKeyService
                .BuildReceiveOnlyConnectionString("other-wallet", store.Id),
            "wrong-store" => BTCPayServer.Plugins.ArkPayServer.Lightning.ArkLightningSpendKeyService
                .BuildReceiveOnlyConnectionString("wallet", "other-store"),
            "unscoped-store" => BTCPayServer.Plugins.ArkPayServer.Lightning.ArkLightningSpendKeyService
                .BuildReceiveOnlyConnectionString("wallet"),
            "missing" => null,
            _ => BTCPayServer.Plugins.ArkPayServer.Lightning.ArkLightningSpendKeyService
                .BuildReceiveOnlyConnectionString("wallet", store.Id)
        };
        if (connectionString is not null)
            store.SetPaymentMethodConfig(lightning, JObject.FromObject(
                new BTCPayServer.Payments.Lightning.LightningPaymentMethodConfig { ConnectionString = connectionString }));
        if (configuration == "excluded")
            store.StoreBlob = "{\"excludedPaymentMethods\":[\"BTC-LN\"]}";
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input["routePolicy"]!["enabledSourceRails"] = new JArray("BTC-LN");
        input["routePolicy"]!["onchainIngressSolver"] = null;

        using var response = await PutConfiguration(client, input);

        Assert.Equal(expectedReady ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (expectedReady) return;
        input["enabled"] = false;
        using var saved = await PutConfiguration(client, input);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewStoreSettings);
        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));
        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.Contains("lightning-payment-method-missing", capabilities["missingConfiguration"]!.Values<string>());
    }

    [Fact]
    public async Task CiphertextCopiedFromAnotherStoreIsReportedUnavailable()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        var protector = app.Services.GetRequiredService<ArkEvmRpcEndpointProtector>();
        var settings = new ArkEvmSettlementSettings(Asset, Destination, true)
        {
            RoutePolicy = CompleteConfiguration()["routePolicy"]!.ToObject<ArkEvmRoutePolicy>(),
            ProtectedRpcUri = protector.Protect("other-store", "https://rpc.example/secret-key")
        };
        var store = app.Services.GetRequiredService<StoreData>();
        var repository = app.Services.GetRequiredService<IArkEvmSettlementStore>();
        await repository.SaveAsync(store, repository.GetConfiguration(store)! with { EvmSettlement = settings });
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);

        var body = await client.GetStringAsync(Endpoint + "/capabilities");
        var capabilities = JObject.Parse(body);

        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.Contains("rpc-endpoint-unavailable", capabilities["missingConfiguration"]!.Values<string>());
        Assert.Null(capabilities.Value<string>("rpcEndpointOrigin"));
        Assert.DoesNotContain("secret-key", body);
        Assert.DoesNotContain(settings.ProtectedRpcUri, body);
    }

    [Fact]
    public async Task UnavailableRpcKeyIsReportedSafelyAndMustBeReplacedBeforeEnabling()
    {
        var policy = CompleteConfiguration()["routePolicy"]!.ToObject<ArkEvmRoutePolicy>()!;
        var initial = new ArkadePaymentMethodConfig("wallet")
        {
            EvmSettlement = new ArkEvmSettlementSettings(Asset, Destination, true)
            {
                RoutePolicy = policy,
                ProtectedRpcUri = "unreadable-secret-ciphertext"
            }
        };
        await using var app = CreateHost(initial);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);
        var body = await client.GetStringAsync(Endpoint);
        var data = JObject.Parse(body);
        Assert.False(data.Value<bool>("configurationComplete"));
        Assert.False(data.Value<bool>("rpcEndpointConfigured"));
        Assert.Contains("rpc-endpoint-unavailable", data["missingConfiguration"]!.Values<string>());
        Assert.DoesNotContain("secret-", body);
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanModifyStoreSettings);
        var update = CompleteConfiguration();
        update.Remove("rpcEndpoint");
        using var rejected = await PutConfiguration(client, update);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var replaced = await PutConfiguration(client, CompleteConfiguration());
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.True(JObject.Parse(await replaced.Content.ReadAsStringAsync()).Value<bool>("configurationComplete"));
    }

    [Theory]
    [InlineData("routePolicy", "null")]
    [InlineData("routePolicy.enabledSourceRails", "[]")]
    [InlineData("routePolicy.enabledSourceRails", "[\"BTC-LN\",\"BTC-LN\"]")]
    [InlineData("routePolicy.enabledSourceRails", "[\"btc-ln\"]")]
    [InlineData("routePolicy.enabledSourceRails", "[\"BTC-LNURL\"]")]
    [InlineData("routePolicy.outgoingSolver", "null")]
    [InlineData("routePolicy.lightningIngressSolver", "null")]
    [InlineData("routePolicy.onchainIngressSolver", "null")]
    [InlineData("routePolicy.swapContractAddress", "\"0x0000000000000000000000000000000000000000\"")]
    [InlineData("routePolicy.fastestSecondsPerBlock", "0")]
    [InlineData("routePolicy.slowestSecondsPerBlock", "0.1")]
    [InlineData("routePolicy.minConfirmations", "0")]
    [InlineData("routePolicy.minAgeSeconds", "0")]
    [InlineData("routePolicy.minimumClaimWindowSeconds", "0")]
    [InlineData("routePolicy.arkadeRefundMarginSeconds", "0")]
    [InlineData("routePolicy.requireEmulatorRefundPath", "false")]
    [InlineData("routePolicy.outgoingSolver", "{\"mode\":\"deployment\"}")]
    [InlineData("routePolicy.outgoingSolver", "{\"mode\":\"registry\",\"endpoint\":\"https://solver.example\"}")]
    [InlineData("routePolicy.outgoingSolver", "{\"mode\":\"registry\",\"discoveryPubkey\":\"secret-invalid-key\"}")]
    [InlineData("routePolicy.outgoingSolver", "{\"mode\":\"explicit\",\"endpoint\":\"wss://solver.example\"}")]
    [InlineData("routePolicy.outgoingSolver.endpoint", "\"https://secret-user:secret-pass@solver.example\"")]
    [InlineData("routePolicy.outgoingSolver.endpoint", "\"https://solver.example?secret-key\"")]
    [InlineData("routePolicy.outgoingSolver.endpoint", "\"https://solver.example/#secret-fragment\"")]
    [InlineData("routePolicy.outgoingSolver.endpoint", "\"file:///secret-path\"")]
    [InlineData("rpcEndpoint", "{\"action\":\"preserve\",\"uri\":\"https://rpc.example/secret-key\"}")]
    [InlineData("rpcEndpoint", "{\"action\":\"clear\",\"uri\":\"https://rpc.example/secret-key\"}")]
    [InlineData("rpcEndpoint", "{\"action\":\"replace\"}")]
    [InlineData("rpcEndpoint", "{\"action\":\"unknown\"}")]
    [InlineData("rpcEndpoint.uri", "\"file:///secret-path\"")]
    [InlineData("rpcEndpoint.uri", "\"https://rpc.example/#secret-fragment\"")]
    public async Task InvalidRoutePolicyIsRejectedWithoutSavingOrReflectingValues(string path, string value)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = CompleteConfiguration();
        input.SelectToken(path)!.Replace(JToken.Parse(value));

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-", await response.Content.ReadAsStringAsync());
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Fact]
    public async Task LegacyEnabledConfigurationIsReadableButIncomplete()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet")
        {
            EvmSettlement = new ArkEvmSettlementSettings(Asset, Destination, true)
        });
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);

        var data = JObject.Parse(await client.GetStringAsync(Endpoint));
        var capabilities = JObject.Parse(await client.GetStringAsync(Endpoint + "/capabilities"));

        Assert.Equal(Asset, data.Value<string>("assetId"));
        Assert.True(data.Value<bool>("enabled"));
        Assert.False(data.Value<bool>("configurationComplete"));
        Assert.Contains("route-policy-missing", data["missingConfiguration"]!.Values<string>());
        Assert.False(capabilities.Value<bool>("configurationComplete"));
        Assert.False(capabilities.Value<bool>("executionAvailable"));
    }

    [Fact]
    public async Task EachRailCanBeConfiguredIndependentlyAndSolversMayBeShared()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        foreach (var rail in new[] { "ARKADE", "BTC-LN", "BTC-CHAIN" })
        {
            var input = CompleteConfiguration();
            var policy = (JObject)input["routePolicy"]!;
            policy["enabledSourceRails"] = new JArray(rail);
            policy["lightningIngressSolver"] = rail == "BTC-LN" ? policy["outgoingSolver"]!.DeepClone() : null;
            policy["onchainIngressSolver"] = rail == "BTC-CHAIN" ? policy["outgoingSolver"]!.DeepClone() : null;

            using var response = await PutConfiguration(client, input);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { rail }, data["routePolicy"]!["enabledSourceRails"]!.Values<string>());
        }
    }

    private static HttpClient AuthorizedClient(string address, string permission = Policies.CanModifyStoreSettings)
    {
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("Test-Permission", permission);
        return client;
    }

    private static Task<HttpResponseMessage> PutConfiguration(HttpClient client, JObject input) =>
        client.PutAsync(Endpoint, new StringContent(input.ToString(), Encoding.UTF8, "application/json"));

    private static JObject CompleteConfiguration() => JObject.Parse("""
        {
          "assetId": "eip155:42161/erc20:0x1111111111111111111111111111111111111111",
          "destination": "0x2222222222222222222222222222222222222222",
          "enabled": true,
          "routePolicy": {
            "enabledSourceRails": ["ARKADE", "BTC-LN", "BTC-CHAIN"],
            "outgoingSolver": {"mode": "explicit", "endpoint": "https://solver.example/proxy"},
            "lightningIngressSolver": {"mode": "registry"},
            "onchainIngressSolver": {"mode": "registry"},
            "swapContractAddress": "0x3333333333333333333333333333333333333333",
            "fastestSecondsPerBlock": 0.25,
            "slowestSecondsPerBlock": 2,
            "minConfirmations": 3,
            "minAgeSeconds": 5,
            "minimumClaimWindowSeconds": 1800,
            "arkadeRefundMarginSeconds": 7200,
            "requireEmulatorRefundPath": true
          },
          "rpcEndpoint": {"action": "replace", "uri": "https://secret-user:secret-pass@rpc.example/secret-path?key=secret-query"},
          "expectedSenderAddress": "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266",
          "maxFeePerGasWei": "100000000000",
          "maxPriorityFeePerGasWei": "2000000000",
          "maxGasLimit": "500000",
          "gasPayerPrivateKey": {"action": "replace", "privateKey": "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80"}
        }
        """);

    private sealed class TestLogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }

    private sealed class InjectedModelState : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context.HttpContext.Request.Headers.ContainsKey("Test-Invalid-Model-State"))
                context.ModelState.AddModelError("unrelated", "Unrelated action validation");
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }

    private sealed class ChunkedJsonContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();

        public override string ToString() => nameof(ChunkedJsonContent);
    }

    private sealed class AbortingBody(CancellationTokenSource cancellation) : MemoryStream
    {
        public bool ReadStarted { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
}
