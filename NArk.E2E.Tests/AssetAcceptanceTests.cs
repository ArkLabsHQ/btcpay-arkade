using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// End-to-end coverage for the store-level "accept an Arkade asset as
/// payment" configuration: the overview row + modal render, and the
/// save path round-trips through the controller's validation and the
/// arkd indexer existence check. A full pay-with-asset settlement flow
/// needs an issued asset funded into the buyer wallet (heavier infra);
/// the deterministic config/validation path is covered here, and the
/// money math is unit-tested in <see cref="AssetRateResolverTests"/>.
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
    /// The Asset Payments row defaults to "Disabled"; opening the modal
    /// and saving an asset id the indexer doesn't know must be rejected
    /// with the indexer-not-found error (config is not persisted).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AssetAcceptance_UnknownAssetId_RejectedWithIndexerError()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        // Defaults to disabled before any configuration.
        var triggerText = await Page!.InnerTextAsync("[data-testid='asset-acceptance-btn']");
        Assert.Contains("Disabled", triggerText);

        // Open the modal and wait for the fields to be interactable.
        await Page.ClickAsync("[data-testid='asset-acceptance-btn']");
        await Page.WaitForSelectorAsync(
            "[data-testid='asset-acceptance-asset-id']",
            new() { State = WaitForSelectorState.Visible });

        // SatsPerUnit needs no reference currency — keeps this test
        // independent of the store's rate configuration.
        await Page.FillAsync("[data-testid='asset-acceptance-asset-id']", "deadbeefnope00");
        await Page.SelectOptionAsync("[data-testid='asset-acceptance-rate-mode']", "SatsPerUnit");
        await Page.FillAsync("[data-testid='asset-acceptance-price']", "10");
        await Page.ClickAsync("[data-testid='asset-acceptance-save']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var body = await Page.InnerTextAsync("body");
        Assert.Contains("not found on the Ark indexer", body, StringComparison.OrdinalIgnoreCase);

        // Rejected config must not have been persisted.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var afterText = await Page.InnerTextAsync("[data-testid='asset-acceptance-btn']");
        Assert.Contains("Disabled", afterText);
    }

    /// <summary>
    /// A fixed-reference-currency config with no reference currency must
    /// be rejected by the controller's <c>IsValid</c> check before any
    /// indexer lookup (client-side JS hides the field, but the server is
    /// the authority).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AssetAcceptance_FixedCurrencyWithoutReferenceCurrency_Rejected()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        await Page!.ClickAsync("[data-testid='asset-acceptance-btn']");
        await Page.WaitForSelectorAsync(
            "[data-testid='asset-acceptance-asset-id']",
            new() { State = WaitForSelectorState.Visible });

        await Page.FillAsync("[data-testid='asset-acceptance-asset-id']", "deadbeefnope00");
        await Page.SelectOptionAsync("[data-testid='asset-acceptance-rate-mode']", "FixedReferenceCurrency");
        await Page.FillAsync("[data-testid='asset-acceptance-price']", "1");
        // Deliberately clear the reference currency, then submit.
        await Page.FillAsync("[data-testid='asset-acceptance-reference-currency']", "");
        await Page.ClickAsync("[data-testid='asset-acceptance-save']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var body = await Page.InnerTextAsync("body");
        Assert.Contains("reference currency is required", body, StringComparison.OrdinalIgnoreCase);
    }
}
