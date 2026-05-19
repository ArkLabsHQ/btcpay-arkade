using BTCPayServer.Plugins.ArkPayServer.Services;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// Pins the canonical base-unit ↔ display conversion shared by the rate
/// resolver and the metadata/display formatter. The high-decimals cases
/// are the regression guard for the old <c>(decimal)Math.Pow(10, n)</c>
/// approach, which loses precision for n &gt; 15 (10^n exceeds the double
/// mantissa) — the decimal-loop <see cref="AssetAmount.Pow10"/> is exact.
/// </summary>
[Trait("Category", "Unit")]
public class AssetAmountTests
{
    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "10")]
    [InlineData(8, "100000000")]
    [InlineData(15, "1000000000000000")]
    [InlineData(18, "1000000000000000000")]
    public void Pow10_IsExact(int exp, string expected)
        => Assert.Equal(expected, AssetAmount.Pow10(exp).ToString(
            System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Pow10_ClampsToAssetRange()
    {
        Assert.Equal(1m, AssetAmount.Pow10(-3));            // < 0 → 10^0
        Assert.Equal(AssetAmount.Pow10(18), AssetAmount.Pow10(25)); // > 18 → 10^18
    }

    [Theory]
    [InlineData(100UL, 0, "100")]              // whole, no decimals (not "1")
    [InlineData(150UL, 2, "1.5")]              // trailing zero trimmed
    [InlineData(100UL, 8, "0.000001")]
    [InlineData(8_000_000UL, 3, "8000")]
    [InlineData(1UL, 18, "0.000000000000000001")] // 1e-18 exact — Math.Pow would drift
    [InlineData(1_000_000_000_000_000_000UL, 18, "1")]
    [InlineData(0UL, 6, "0")]
    public void Format_RendersExpected(ulong baseUnits, int decimals, string expected)
        => Assert.Equal(expected, AssetAmount.Format(baseUnits, decimals));
}
