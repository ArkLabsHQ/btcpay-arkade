using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkPaymentProofDisplayTests
{
    [Fact]
    public void CompositionPaymentUsesEvmClaimTransactionWithoutArkOutpointInterpretation()
    {
        var routeId = Guid.NewGuid();
        var transactionId = "0x" + new string('a', 64);
        var payment = new ArkadePaymentData($"evm:{routeId:N}", "ark-destination");

        var display = ArkPaymentProofDisplay.From(payment, new JObject
        {
            ["compositionRouteId"] = routeId.ToString("N"), ["evmClaimTransactionId"] = transactionId
        });

        Assert.True(display.IsEvmSettlement);
        Assert.Equal(transactionId, display.Identifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("javascript:alert(1)")]
    [InlineData("0xnot-a-transaction")]
    public void MissingOrMalformedClaimUsesOnlyCanonicalRouteIdentifier(string? transactionId)
    {
        var routeId = Guid.NewGuid();
        var display = ArkPaymentProofDisplay.From(new ArkadePaymentData("invalid-ark-outpoint"), new JObject
        {
            ["compositionRouteId"] = routeId.ToString(), ["evmClaimTransactionId"] = transactionId
        });

        Assert.True(display.IsEvmSettlement);
        Assert.Equal($"Route {routeId:N}", display.Identifier);
    }

    [Theory]
    [InlineData("compositionRouteId")]
    [InlineData("evmClaimTransactionId")]
    public void AnyCompositionMetadataSuppressesArkLinksEvenWhenProofIsMalformed(string key)
    {
        var display = ArkPaymentProofDisplay.From(new ArkadePaymentData("invalid-ark-outpoint"),
            new JObject { [key] = new JObject { ["untrusted"] = "not-a-proof" } });

        Assert.True(display.IsEvmSettlement);
        Assert.Equal("Unavailable", display.Identifier);
    }

    [Fact]
    public void OldSyntheticEvmPaymentIdDoesNotBecomeAnArkExplorerLink()
    {
        var display = ArkPaymentProofDisplay.From(new ArkadePaymentData($"evm:{Guid.NewGuid():N}"), new JObject());

        Assert.True(display.IsEvmSettlement);
        Assert.Equal("Unavailable", display.Identifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryArkadeAndBoardingPaymentsKeepTheirOriginalOutpoint(bool boarding)
    {
        var outpoint = new string('b', 64) + ":2";
        var display = ArkPaymentProofDisplay.From(new ArkadePaymentData(outpoint, "destination", boarding), new JObject());

        Assert.False(display.IsEvmSettlement);
        Assert.Equal(outpoint, display.Identifier);
    }
}
