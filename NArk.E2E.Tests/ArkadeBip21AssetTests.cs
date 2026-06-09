using System;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// BIP-321 asset-URI contract for <see cref="ArkadeBip21Builder.WithAsset"/>
/// (pure; no infra). An asset option is ark-only and carries the asset id plus
/// the amount due denominated in the asset's own display units.
/// </summary>
[Trait("Category", "Unit")]
public class ArkadeBip21AssetTests
{
    [Fact]
    public void WithAsset_appends_asset_id_and_asset_amount()
    {
        var uri = ArkadeBip21Builder.Create()
            .WithArkAddress("tark1qexample")
            .WithAsset("deadbeef", 1.5m)
            .Build();

        Assert.StartsWith("bitcoin:?", uri);          // ark-only: no onchain address in the path
        Assert.Contains("ark=tark1qexample", uri);
        Assert.Contains("asset=deadbeef", uri);
        Assert.Contains("amount=1.5", uri);           // amount denominates the asset when asset is present
        Assert.DoesNotContain("lightning=", uri);
    }

    [Fact]
    public void WithAsset_requires_non_empty_asset_id()
    {
        Assert.Throws<ArgumentException>(() =>
            ArkadeBip21Builder.Create().WithArkAddress("tark1qexample").WithAsset("", 1m));
    }
}
