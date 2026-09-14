using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkEvmSettlementSettingsTests
{
    private const string Asset = "eip155:42161/erc20:0x1111111111111111111111111111111111111111";
    private const string Destination = "0x2222222222222222222222222222222222222222";
    private readonly ArkadePaymentMethodHandler _handler = new(null!, null!, null!, null!, null!);

    [Fact]
    public void WatchOnlyStoreSettingsRoundTripWithoutKeepingUnknownSecretFields()
    {
        var input = JObject.FromObject(new
        {
            WalletId = "public-wallet",
            GeneratedByStore = false,
            EvmSettlement = new { AssetId = Asset, Destination, Enabled = true, Preimage = "secret-must-not-survive" }
        });

        var parsed = _handler.ParsePaymentMethodConfig(input);
        var stored = JObject.FromObject(parsed, _handler.Serializer);

        var settlement = Assert.IsType<JObject>(stored.GetValue("evmSettlement", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Asset, settlement.GetValue("assetId", StringComparison.OrdinalIgnoreCase)?.Value<string>());
        Assert.Equal(Destination, settlement.GetValue("destination", StringComparison.OrdinalIgnoreCase)?.Value<string>());
        Assert.True(settlement.GetValue("enabled", StringComparison.OrdinalIgnoreCase)?.Value<bool>());
        Assert.DoesNotContain("secret-must-not-survive", stored.ToString());
    }

    [Theory]
    [InlineData("btc", Destination)]
    [InlineData("eip155:0/erc20:0x1111111111111111111111111111111111111111", Destination)]
    [InlineData("eip155:01/erc20:0x1111111111111111111111111111111111111111", Destination)]
    [InlineData("eip155:111111111111111111111111111111111/erc20:0x1111111111111111111111111111111111111111", Destination)]
    [InlineData("eip155:42161/erc20:0x0000000000000000000000000000000000000000", Destination)]
    [InlineData(Asset, "0x0000000000000000000000000000000000000000")]
    [InlineData(Asset, "nsec-secret-must-not-be-reflected")]
    public void InvalidSettlementDestinationOrAssetIsRejectedWithoutEchoingInput(string asset, string destination)
    {
        var input = JObject.FromObject(new
        {
            WalletId = "wallet",
            EvmSettlement = new { AssetId = asset, Destination = destination, Enabled = true }
        });

        var error = Assert.Throws<ArgumentException>(() => _handler.ParsePaymentMethodConfig(input));

        Assert.DoesNotContain("nsec-secret", error.Message);
    }

    [Fact]
    public void MaximumCaipChainReferenceIsAccepted()
    {
        var asset = "eip155:" + new string('1', 32) + "/erc20:0x1111111111111111111111111111111111111111";

        Assert.Equal(asset, new ArkEvmSettlementSettings(asset, Destination).Validate().AssetId);
    }

    [Fact]
    public void ExistingStoreConfigurationRemainsReadable()
    {
        var config = Assert.IsType<ArkadePaymentMethodConfig>(_handler.ParsePaymentMethodConfig(
            JObject.Parse("{\"walletId\":\"legacy-wallet\",\"generatedByStore\":false,\"boardingEnabled\":true}")));

        Assert.Equal("legacy-wallet", config.WalletId);
        Assert.True(config.BoardingEnabled);
    }
}
