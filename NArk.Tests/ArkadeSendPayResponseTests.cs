using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkadeSendPayResponseTests
{
    private const string Invoice =
        "lnbcrt21u1p5tqtaypp56yzglgfgwsm5pd49996jqvtmpf8fqdk7cq2znnjw5c2j5t8ua38qdql2djkuepqw3hjqs2jfvsxzerywfjhxuccqz95xqztfsp586s5vpsdxt05rm7hr6ycwq5ffmnx2gngv820seugky6j6z2wxqwq9qxpqysgqepuxr82pvlp8lgj7nqu8yp2f5q32323jxddx9qgtjhfhsyzvftgkwx8qv4772fzz46pwyw5ex3u7lf7na8a8403ur3gyeu22gv29rpspefzz2y";

    private static readonly BOLT11PaymentRequest Pr = BOLT11PaymentRequest.Parse(Invoice, Network.RegTest);

    private static ArkadeSwapIntent Send(ArkadeSwapIntentStatus status) => new ArkadeSwapIntent
    {
        Id = "swap-1",
        WalletId = "wallet-1",
        Type = ArkadeSwapIntentType.BtcToLightning,
        OfferAmount = Money.Satoshis(2_150),
        WantAmount = Money.Satoshis(2_100),
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = "5120" + new string('a', 64),
        SwapAddress = "tark1example",
        PaymentHash = Pr.PaymentHash!.ToString(),
    }.WithLightningMetadata(new LightningSwapMetadata(Invoice, null));

    [Theory]
    [InlineData(ArkadeSwapIntentStatus.Funding)]
    [InlineData(ArkadeSwapIntentStatus.Pending)]
    [InlineData(ArkadeSwapIntentStatus.Refundable)]
    [InlineData(ArkadeSwapIntentStatus.Resolved)]
    [InlineData(ArkadeSwapIntentStatus.Cancelled)]
    public void AFundedSendThatHasNotSettled_IsUnknownNeverError(ArkadeSwapIntentStatus status)
    {
        var response = ArkadeIntentLightningMapper.ToPayResponse(Send(status), Pr, Network.RegTest);

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(Pr.PaymentHash, response.Details.PaymentHash);
    }

    [Fact]
    public void ASettledSend_IsOk()
    {
        var response = ArkadeIntentLightningMapper.ToPayResponse(
            Send(ArkadeSwapIntentStatus.Fulfilled), Pr, Network.RegTest);

        Assert.Equal(PayResult.Ok, response.Result);
        Assert.Equal(LightningPaymentStatus.Complete, response.Details.Status);
    }

    [Fact]
    public void AnUnknownFundingOutcome_IsInFlightWithItsPaymentHash()
    {
        var response = ArkadeIntentLightningMapper.InFlight(Pr, "boom");

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(LightningPaymentStatus.Pending, response.Details.Status);
        Assert.Equal(Pr.PaymentHash, response.Details.PaymentHash);
        Assert.Equal("boom", response.ErrorDetail);
    }

    [Theory]
    [InlineData(ArkadeSwapIntentStatus.Refundable)]
    [InlineData(ArkadeSwapIntentStatus.Resolved)]
    [InlineData(ArkadeSwapIntentStatus.Recoverable)]
    public void ASendNotProvenRefunded_IsStillPending(ArkadeSwapIntentStatus status)
    {
        Assert.Equal(LightningPaymentStatus.Pending,
            ArkadeIntentLightningMapper.ToPayment(Send(status), Network.RegTest).Status);
    }

    [Fact]
    public void ASendWhoseRefundLanded_IsFailed()
    {
        Assert.Equal(LightningPaymentStatus.Failed,
            ArkadeIntentLightningMapper.ToPayment(Send(ArkadeSwapIntentStatus.Cancelled), Network.RegTest).Status);
    }
}
