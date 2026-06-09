using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// End-to-end coverage for the store-level tracked-Arkade-asset configuration:
/// the overview "Asset Payments" row + the add/edit modal render, and the
/// add-asset path round-trips through the controller's validation and the arkd
/// indexer existence check. A full add → pay-with-asset settlement flow needs an
/// issued asset funded into the buyer wallet (heavier infra); the deterministic
/// config/validation paths are covered here, the money math is unit-tested in
/// <see cref="AssetRateResolverTests"/>, and the BIP-321 asset URI in
/// <see cref="ArkadeBip21AssetTests"/>.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class AssetAcceptanceTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public AssetAcceptanceTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The Asset Payments row defaults to "Disabled"; opening the modal and
    /// adding an asset id the indexer doesn't know must be rejected with the
    /// indexer-not-found error (config is not persisted, so the row stays
    /// "Disabled").
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackedAsset_UnknownAssetId_RejectedWithIndexerError()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        // Defaults to disabled before any asset is tracked.
        var triggerText = await Page!.InnerTextAsync("[data-testid='asset-acceptance-btn']");
        Assert.Contains("Disabled", triggerText);

        // Open the modal and wait for the add-asset form to be interactable.
        await Page.ClickAsync("[data-testid='asset-acceptance-btn']");
        await Page.WaitForSelectorAsync(
            "[data-testid='asset-form-asset-id']",
            new() { State = WaitForSelectorState.Visible });

        // A well-formed rate script keeps this independent of indexer rate data;
        // the asset id simply doesn't exist, so the indexer check must reject it.
        await Page.FillAsync("[data-testid='asset-form-asset-id']", "deadbeefnope00");
        await Page.FillAsync("[data-testid='asset-form-currency-code']", "NOPE");
        await Page.FillAsync("[data-testid='asset-form-rate-script']", "NOPE_USD = 1;");
        await Page.ClickAsync("[data-testid='asset-form-submit']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var body = await Page.InnerTextAsync("body");
        Assert.Contains("was not found on the Arkade indexer", body, StringComparison.OrdinalIgnoreCase);

        // Rejected config must not have been persisted.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var afterText = await Page.InnerTextAsync("[data-testid='asset-acceptance-btn']");
        Assert.Contains("Disabled", afterText);
    }

    /// <summary>
    /// An add-asset submission with an empty rate script must be rejected by the
    /// controller's <c>TrackedArkadeAsset.IsValid</c> check before any indexer
    /// lookup (the server is the authority, regardless of client-side hints).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackedAsset_EmptyRateScript_Rejected()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        await Page!.ClickAsync("[data-testid='asset-acceptance-btn']");
        await Page.WaitForSelectorAsync(
            "[data-testid='asset-form-asset-id']",
            new() { State = WaitForSelectorState.Visible });

        await Page.FillAsync("[data-testid='asset-form-asset-id']", "deadbeefnope00");
        await Page.FillAsync("[data-testid='asset-form-currency-code']", "NOPE");
        // Deliberately leave the rate script empty, then submit.
        await Page.FillAsync("[data-testid='asset-form-rate-script']", "");
        await Page.ClickAsync("[data-testid='asset-form-submit']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var body = await Page.InnerTextAsync("body");
        Assert.Contains("rate script is required", body, StringComparison.OrdinalIgnoreCase);
    }
}
