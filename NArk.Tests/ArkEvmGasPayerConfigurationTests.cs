using System.Net;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public partial class ArkEvmSettlementApiTests
{
    private const string GasPayerKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string GasPayerAddress = "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266";

    [Fact]
    public async Task GasPayerReplacementIsStoreProtectedAndNeverReturned()
    {
        using var logs = new TestLogSink();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"), logSink: logs);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = ConfigurationWithGasPayer();
        input["expectedSenderAddress"] = "0x" + GasPayerAddress[2..].ToUpperInvariant();

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var putBody = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(putBody);
        Assert.True(data.Value<bool>("gasPayerConfigured"));
        Assert.True(data.Value<bool>("configurationComplete"));
        Assert.Equal(GasPayerAddress, data.Value<string>("expectedSenderAddress"));
        Assert.Equal("100000000000", data.Value<string>("maxFeePerGasWei"));
        Assert.Equal("2000000000", data.Value<string>("maxPriorityFeePerGasWei"));
        Assert.Equal("500000", data.Value<string>("maxGasLimit"));
        var store = app.Services.GetRequiredService<StoreData>();
        var settings = app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement!;
        Assert.NotNull(settings.ProtectedGasPayerPrivateKey);
        Assert.NotEqual(GasPayerKey, settings.ProtectedGasPayerPrivateKey);
        Assert.True(new ArkEvmGasPayerProtector(app.Services.GetRequiredService<IDataProtectionProvider>())
            .IsAvailable(store.Id, settings.ProtectedGasPayerPrivateKey, GasPayerAddress));
        foreach (var output in new[] { putBody, settings.ToString(), store.GetPaymentMethodConfigs().ToString() })
        {
            Assert.DoesNotContain(GasPayerKey, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(settings.ProtectedGasPayerPrivateKey, putBody);
            Assert.DoesNotContain("gasPayerPrivateKey", output, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(logs.Messages, line => Assert.DoesNotContain(GasPayerKey, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GasPayerCanBePreservedOrClearedOnlyWhileDisabled()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        using var created = await PutConfiguration(client, ConfigurationWithGasPayer());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var store = app.Services.GetRequiredService<StoreData>();
        var original = app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement!
            .ProtectedGasPayerPrivateKey;
        var preserve = ConfigurationWithGasPayer();
        preserve["gasPayerPrivateKey"] = JObject.Parse("{\"action\":\"preserve\"}");

        using var preserved = await PutConfiguration(client, preserve);

        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.Equal(original, app.Services.GetRequiredService<IArkEvmSettlementStore>()
            .GetConfiguration(store)!.EvmSettlement!.ProtectedGasPayerPrivateKey);
        preserve["gasPayerPrivateKey"] = JObject.Parse("{\"action\":\"clear\"}");
        using var rejected = await PutConfiguration(client, preserve);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        preserve["enabled"] = false;
        using var cleared = await PutConfiguration(client, preserve);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var data = JObject.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.False(data.Value<bool>("gasPayerConfigured"));
        Assert.Contains("gas-payer-key-missing", data["missingConfiguration"]!.Values<string>());
    }

    [Fact]
    public async Task KeyMustDeriveConfiguredSenderWithoutEchoingEitherValue()
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = ConfigurationWithGasPayer();
        input["expectedSenderAddress"] = "0x1111111111111111111111111111111111111111";

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(GasPayerKey, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x1111111111111111111111111111111111111111", body, StringComparison.OrdinalIgnoreCase);
        var store = app.Services.GetRequiredService<StoreData>();
        Assert.Null(app.Services.GetRequiredService<IArkEvmSettlementStore>().GetConfiguration(store)!.EvmSettlement);
    }

    [Theory]
    [InlineData("expectedSenderAddress", "\"0x0000000000000000000000000000000000000000\"")]
    [InlineData("maxFeePerGasWei", "\"0\"")]
    [InlineData("maxFeePerGasWei", "\"01\"")]
    [InlineData("maxFeePerGasWei", "\"-1\"")]
    [InlineData("maxFeePerGasWei", "\"115792089237316195423570985008687907853269984665640564039457584007913129639936\"")]
    [InlineData("maxPriorityFeePerGasWei", "\"100000000001\"")]
    [InlineData("maxGasLimit", "\"0\"")]
    [InlineData("gasPayerPrivateKey", "{\"action\":\"replace\",\"privateKey\":\"00\"}")]
    [InlineData("gasPayerPrivateKey", "{\"action\":\"replace\",\"privateKey\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    [InlineData("gasPayerPrivateKey", "{\"action\":\"preserve\",\"privateKey\":\"secret-key\"}")]
    [InlineData("gasPayerPrivateKey", "{\"action\":\"clear\",\"privateKey\":\"secret-key\"}")]
    public async Task InvalidGasPayerConfigurationIsRejected(string path, string value)
    {
        await using var app = CreateHost(new ArkadePaymentMethodConfig("wallet"));
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single());
        var input = ConfigurationWithGasPayer();
        input.SelectToken(path)!.Replace(JToken.Parse(value));

        using var response = await PutConfiguration(client, input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnavailableGasPayerCiphertextIsReportedWithoutExposure()
    {
        var initial = new ArkadePaymentMethodConfig("wallet")
        {
            EvmSettlement = new ArkEvmSettlementSettings(Asset, Destination, true)
            {
                RoutePolicy = CompleteConfiguration()["routePolicy"]!.ToObject<ArkEvmRoutePolicy>(),
                ProtectedRpcUri = new ArkEvmRpcEndpointProtector(new EphemeralDataProtectionProvider())
                    .Protect("store", "https://rpc.example"),
                ProtectedGasPayerPrivateKey = "unavailable-secret-ciphertext",
                ExpectedSenderAddress = GasPayerAddress,
                MaxFeePerGasWei = "100000000000",
                MaxPriorityFeePerGasWei = "2000000000",
                MaxGasLimit = "500000"
            }
        };
        await using var app = CreateHost(initial);
        await app.StartAsync();
        using var client = AuthorizedClient(app.Urls.Single(), Policies.CanViewStoreSettings);

        var body = await client.GetStringAsync(Endpoint);
        var data = JObject.Parse(body);

        Assert.False(data.Value<bool>("gasPayerConfigured"));
        Assert.False(data.Value<bool>("configurationComplete"));
        Assert.Contains("gas-payer-key-unavailable", data["missingConfiguration"]!.Values<string>());
        Assert.DoesNotContain("unavailable-secret-ciphertext", body);
    }

    [Fact]
    public void GasPayerCiphertextIsBoundToStoreAndExpectedAddress()
    {
        var protector = new ArkEvmGasPayerProtector(new EphemeralDataProtectionProvider());
        var ciphertext = protector.Protect("store", GasPayerKey, GasPayerAddress);

        Assert.True(protector.IsAvailable("store", ciphertext, GasPayerAddress));
        Assert.False(protector.IsAvailable("other", ciphertext, GasPayerAddress));
        Assert.False(protector.IsAvailable("store", ciphertext, "0x1111111111111111111111111111111111111111"));
        Assert.False(protector.IsAvailable("store", "invalid-ciphertext", GasPayerAddress));
        Assert.DoesNotContain(GasPayerKey, protector.ToString(), StringComparison.OrdinalIgnoreCase);

        ReadOnlyMemory<byte> observed = default;
        protector.UseKey("store", ciphertext, GasPayerAddress, key =>
        {
            observed = key;
            Assert.Equal(GasPayerKey, Convert.ToHexString(key.Span).ToLowerInvariant());
            return true;
        });
        Assert.All(observed.ToArray(), value => Assert.Equal(0, value));
    }

    private static JObject ConfigurationWithGasPayer()
    {
        var input = CompleteConfiguration();
        input["expectedSenderAddress"] = GasPayerAddress;
        input["maxFeePerGasWei"] = "100000000000";
        input["maxPriorityFeePerGasWei"] = "2000000000";
        input["maxGasLimit"] = "500000";
        input["gasPayerPrivateKey"] = new JObject
        {
            ["action"] = "replace",
            ["privateKey"] = GasPayerKey
        };
        return input;
    }
}
