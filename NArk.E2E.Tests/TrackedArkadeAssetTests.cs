using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>Validation contract for <see cref="TrackedArkadeAsset"/> (pure; no infra).</summary>
[Trait("Category", "Unit")]
public class TrackedArkadeAssetTests
{
    private static TrackedArkadeAsset Valid() =>
        new("abc123", "USDARK", "USDARK", "USD Arkade", Decimals: 2,
            RateScript: "USDARK_USD = 1;", Enabled: true);

    [Fact]
    public void Valid_config_passes()
    {
        Assert.True(Valid().IsValid(out var err));
        Assert.Null(err);
    }

    [Fact]
    public void Missing_asset_id_fails()
    {
        Assert.False((Valid() with { AssetId = "" }).IsValid(out var err));
        Assert.Contains("asset id", err);
    }

    [Fact]
    public void Missing_currency_code_fails() =>
        Assert.False((Valid() with { CurrencyCode = " " }).IsValid(out _));

    [Fact]
    public void Empty_rate_script_fails() =>
        Assert.False((Valid() with { RateScript = "" }).IsValid(out _));

    [Theory]
    [InlineData(-1)]
    [InlineData(19)]
    public void Out_of_range_decimals_fails(int decimals) =>
        Assert.False((Valid() with { Decimals = decimals }).IsValid(out _));
}
