using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents.Rfq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>Who covers the solver's spread on a receive swap.</summary>
public enum ArkadeSwapFeePayer
{
    /// <summary>The store: the payer is billed the order amount and the fee comes out of the payout.</summary>
    Recipient,

    /// <summary>The payer: the payout is the order amount and the payer is billed the fee on top.</summary>
    Sender,
}

public static class ArkadeSwapFeePayerSetting
{
    public const string MetadataKey = "arkade.swapFeePayer";

    // What the Boltz-era reverse-swap toggle wrote; read so an upgraded store keeps its choice.
    private const string LegacyMetadataKey = "arkade.reverseSwapFeePayer";

    public static ArkadeSwapFeePayer Read(ArkWalletInfo? wallet) =>
        Parse(Value(wallet, MetadataKey) ?? Value(wallet, LegacyMetadataKey));

    public static async Task<ArkadeSwapFeePayer> ReadAsync(
        IWalletStorage wallets, string walletId, CancellationToken cancellationToken = default) =>
        Read(await wallets.GetWalletById(walletId, cancellationToken));

    /// <summary>Which leg of the swap the order amount pins.</summary>
    public static RfqAmountSide AmountSide(ArkadeSwapFeePayer payer) =>
        payer == ArkadeSwapFeePayer.Recipient ? RfqAmountSide.From : RfqAmountSide.To;

    private static string? Value(ArkWalletInfo? wallet, string key) =>
        wallet?.Metadata?.TryGetValue(key, out var raw) is true ? raw : null;

    private static ArkadeSwapFeePayer Parse(string? raw) =>
        Enum.TryParse<ArkadeSwapFeePayer>(raw, out var payer) ? payer : ArkadeSwapFeePayer.Recipient;
}
