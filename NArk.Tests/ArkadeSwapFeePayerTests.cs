using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents.Rfq;
using Xunit;

namespace NArk.Tests;

public class ArkadeSwapFeePayerTests
{
    private static ArkWalletInfo Wallet(params (string Key, string Value)[] metadata) =>
        new("wallet-1", null, null, WalletType.HD, null, 0,
            metadata.ToDictionary(m => m.Key, m => m.Value));

    [Fact]
    public void WithNothingConfigured_TheStorePaysTheFee()
    {
        Assert.Equal(ArkadeSwapFeePayer.Recipient, ArkadeSwapFeePayerSetting.Read(null));
        Assert.Equal(ArkadeSwapFeePayer.Recipient, ArkadeSwapFeePayerSetting.Read(Wallet()));
    }

    [Fact]
    public void TheBoltzEraSettingIsStillHonoured()
    {
        Assert.Equal(ArkadeSwapFeePayer.Sender,
            ArkadeSwapFeePayerSetting.Read(Wallet(("arkade.reverseSwapFeePayer", "Sender"))));
    }

    [Fact]
    public void TheCurrentSettingWins()
    {
        Assert.Equal(ArkadeSwapFeePayer.Recipient, ArkadeSwapFeePayerSetting.Read(Wallet(
            ("arkade.swapFeePayer", "Recipient"), ("arkade.reverseSwapFeePayer", "Sender"))));
    }

    [Fact]
    public void AnUnreadableSettingFallsBackToTheSaferSide()
    {
        Assert.Equal(ArkadeSwapFeePayer.Recipient,
            ArkadeSwapFeePayerSetting.Read(Wallet(("arkade.swapFeePayer", "nonsense"))));
    }

    [Fact]
    public void TheOrderAmountPinsThePayersLegWhenTheStorePays()
    {
        Assert.Equal(RfqAmountSide.From, ArkadeSwapFeePayerSetting.AmountSide(ArkadeSwapFeePayer.Recipient));
        Assert.Equal(RfqAmountSide.To, ArkadeSwapFeePayerSetting.AmountSide(ArkadeSwapFeePayer.Sender));
    }

    [Fact]
    public void EveryRailOfThePaymentLinkSharesOneAmount()
    {
        // The swap is only offered when its HTLC asks for the order amount, so nothing has to be left out.
        var link = ArkadeBip21Builder.Create().WithArkAddress("ark1abc")
            .WithOnchainAddress("bcrt1qhtlc").WithAmount(0.0005m).Build();

        Assert.Equal("bitcoin:bcrt1qhtlc?amount=0.0005&ark=ark1abc", link);
    }
}
