namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Canonical Arkade-asset base-unit ↔ display conversions. One
/// implementation so the rate resolver (<see cref="AssetRateResolver"/>)
/// and the metadata/display formatter (<see cref="AssetMetadataService"/>)
/// can never disagree on a divisor or on rendering.
/// </summary>
public static class AssetAmount
{
    /// <summary>
    /// 10^<paramref name="exp"/> as an exact <see cref="decimal"/>, with
    /// <paramref name="exp"/> clamped to 0..18 (the asset-decimals range).
    /// Uses a decimal loop, not <see cref="System.Math.Pow"/>: for
    /// exp &gt; 15, <c>10^exp</c> exceeds the <see cref="double"/> mantissa
    /// (2^53 ≈ 9e15) and casting the result to decimal would bake in the
    /// rounding error.
    /// </summary>
    public static decimal Pow10(int exp)
    {
        exp = System.Math.Clamp(exp, 0, 18);
        decimal result = 1m;
        for (var i = 0; i < exp; i++)
            result *= 10m;
        return result;
    }

    /// <summary>
    /// Formats a raw base-unit amount using the asset's declared decimals:
    /// no trailing zeros, no trailing dot, and always at least the integer
    /// part (150 with decimals=2 → "1.5"; 100 with decimals=0 → "100";
    /// 100 base units with decimals=8 → "0.000001").
    /// </summary>
    public static string Format(ulong baseUnits, int decimals)
    {
        if (decimals <= 0)
            return baseUnits.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var value = baseUnits / Pow10(decimals);
        return value.ToString(
            "0." + new string('#', System.Math.Clamp(decimals, 1, 18)),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
