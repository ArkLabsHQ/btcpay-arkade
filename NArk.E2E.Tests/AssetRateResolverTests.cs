using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// Money-math contract for <see cref="AssetRateResolver"/>. The
/// <see cref="AssetRateMode.SatsPerUnit"/> path and the input guards touch
/// neither BTCPay's rate pipeline nor the store, so these run standalone
/// (no fixture / no docker). They pin the rules that, if wrong, would
/// underpay the merchant or mint a zero-amount asset output. The
/// <see cref="AssetRateMode.FixedReferenceCurrency"/> rate-fetch leg is
/// exercised end-to-end by the integration suite against a real store.
/// </summary>
[Trait("Category", "Unit")]
public class AssetRateResolverTests
{
    // SatsPerUnit + the validation guards never dereference these.
    private static AssetRateResolver NewResolver() => new(null!, null!);

    [Theory]
    // due sats, sats/unit, decimals => expected base units, expected display
    [InlineData(1000, 10, 0, 100UL, "100")]      // exact, no decimals
    [InlineData(1000, 10, 2, 10000UL, "100")]    // 100 whole units, 2 decimals → 10000 base
    [InlineData(1005, 10, 0, 101UL, "101")]      // 100.5 units → round UP (never underpay)
    [InlineData(50, 100, 0, 1UL, "1")]           // 0.5 units, 0 decimals → round up to 1 base unit
    [InlineData(1, 1000000, 8, 100UL, "0.000001")] // 1 sat → 0.000001 unit = 100 base units (8 dp)
    public async Task SatsPerUnit_RoundsUp_AndClampsToOneBaseUnit(
        long dueSats, decimal satsPerUnit, int decimals,
        ulong expectedBaseUnits, string expectedFormatted)
    {
        var acceptance = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.SatsPerUnit, satsPerUnit);

        var result = await NewResolver().ResolveAsync(
            new StoreData(), acceptance, dueSats, decimals, CancellationToken.None);

        Assert.Equal(expectedBaseUnits, result.BaseUnits);
        Assert.Equal(expectedFormatted, result.FormattedAmount);
        Assert.False(string.IsNullOrWhiteSpace(result.RateDescription));
    }

    [Fact]
    public async Task SatsPerUnit_BaseUnits_NeverRoundDownBelowDue()
    {
        // 1 sat over an exact boundary must bump a whole base unit so the
        // merchant is paid at least the invoice value.
        var acceptance = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.SatsPerUnit, 10m);

        var exact = await NewResolver().ResolveAsync(
            new StoreData(), acceptance, 1000, 0, CancellationToken.None);
        var over = await NewResolver().ResolveAsync(
            new StoreData(), acceptance, 1001, 0, CancellationToken.None);

        Assert.Equal(100UL, exact.BaseUnits);
        Assert.Equal(101UL, over.BaseUnits);
    }

    [Fact]
    public async Task InvalidConfig_Throws_BeforeAnyRateLookup()
    {
        // PricePerUnit <= 0 fails IsValid; resolver must reject (not divide
        // by zero / not hit the null RateFetcher).
        var bad = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.SatsPerUnit, 0m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewResolver().ResolveAsync(
                new StoreData(), bad, 1000, 0, CancellationToken.None));
    }

    [Fact]
    public async Task FixedReferenceCurrency_MissingReferenceCurrency_Throws_NoRateLookup()
    {
        // IsValid rejects a fixed-currency config with no reference
        // currency; this must surface before the (null) RateFetcher.
        var bad = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.FixedReferenceCurrency, 1m, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewResolver().ResolveAsync(
                new StoreData(), bad, 1000, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveDue_Throws(long dueSats)
    {
        var acceptance = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.SatsPerUnit, 10m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewResolver().ResolveAsync(
                new StoreData(), acceptance, dueSats, 0, CancellationToken.None));
    }

    [Fact]
    public async Task SatsPerUnit_FractionalPrice_Supported()
    {
        // A sub-sat per-unit price (cheap token): 0.5 sats/unit, 4000 sats
        // due → 8000 whole units. Decimals scale into base units.
        var acceptance = new ArkadeAssetAcceptance(
            "deadbeef00", AssetRateMode.SatsPerUnit, 0.5m);

        var result = await NewResolver().ResolveAsync(
            new StoreData(), acceptance, 4000, 3, CancellationToken.None);

        Assert.Equal(8_000_000UL, result.BaseUnits); // 8000 * 10^3
        Assert.Equal("8000", result.FormattedAmount);
    }
}
