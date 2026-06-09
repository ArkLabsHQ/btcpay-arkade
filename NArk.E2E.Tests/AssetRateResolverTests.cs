using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// Settlement money-math contract for Arkade assets, and the resolver's input
/// guards. The money-math (round-up, clamp-to-one-base-unit, decimal scaling)
/// is pure (<see cref="AssetAmount.BaseUnitsDue"/>), and the guards reject
/// before any rate lookup, so these run standalone (no fixture / no docker).
/// The rate-fetch leg (free-form script → BTC→asset rate via RateFetcher) is
/// exercised end-to-end by the integration suite against a real store.
/// </summary>
[Trait("Category", "Unit")]
public class AssetRateResolverTests
{
    [Theory]
    // displayUnits (whole asset units due), decimals => base units, formatted
    [InlineData("100", 0, 100UL, "100")]
    [InlineData("100", 2, 10000UL, "100")]
    [InlineData("100.5", 0, 101UL, "101")]        // round UP — never underpay the merchant
    [InlineData("0.5", 0, 1UL, "1")]              // clamp to >= 1 base unit
    [InlineData("0.000001", 8, 100UL, "0.000001")]
    [InlineData("8000", 3, 8000000UL, "8000")]
    public void BaseUnitsDue_RoundsUp_ClampsToOne_ScalesByDecimals(
        string displayUnits, int decimals, ulong expectedBaseUnits, string expectedFormatted)
    {
        var (baseUnits, _) = AssetAmount.BaseUnitsDue(
            decimal.Parse(displayUnits, CultureInfo.InvariantCulture), decimals);

        Assert.Equal(expectedBaseUnits, baseUnits);
        Assert.Equal(expectedFormatted, AssetAmount.Format(baseUnits, decimals));
    }

    [Fact]
    public async Task InvalidAsset_Throws_BeforeAnyRateLookup()
    {
        // Empty rate script fails IsValid; the resolver must reject before it
        // ever touches the (null) RateFetcher.
        var bad = new TrackedArkadeAsset("deadbeef00", "MYA", "MYA", "My Asset",
            Decimals: 0, RateScript: "", Enabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AssetRateResolver(null!, null!).ResolveAsync(
                new StoreData(), bad, dueSats: 1000, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveDue_Throws_BeforeAnyRateLookup(long dueSats)
    {
        var asset = new TrackedArkadeAsset("deadbeef00", "MYA", "MYA", "My Asset",
            Decimals: 0, RateScript: "MYA_BTC = 100000;", Enabled: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AssetRateResolver(null!, null!).ResolveAsync(
                new StoreData(), asset, dueSats, CancellationToken.None));
    }
}
