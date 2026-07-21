using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Covers the "Reverse Swap Fee" toggle on the Arkade store overview
/// (<see cref="ArkLightningClient.ReverseSwapFeePayerMetadataKey"/>). The
/// setting lives on the wallet's metadata, not the store's payment method
/// config, so it must be shared by every store pointed at the same wallet.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class ReverseSwapFeePayerTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ReverseSwapFeePayerTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Two stores import the same nsec, so both reference the same wallet
    /// (wallet id is deterministic from the descriptor — see
    /// WalletFactory). Toggling the fee payer on store A must be visible
    /// on store B, and must persist in the wallet's Metadata bag, proving
    /// the setting is wallet-level rather than duplicated per store.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ToggleFeePayer_IsSharedAcrossStoresOnTheSameWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var nsec = GenerateRandomNsec();
        var storeAId = await CreateStoreWithArkWalletAsync(nsec);
        var walletId = await GetStoreWalletIdAsync(storeAId);
        Assert.False(string.IsNullOrWhiteSpace(walletId), "store A has no wallet id");

        var storeBId = await CreateStoreWithArkWalletAsync(walletId);
        Assert.NotEqual(storeAId, storeBId);

        // Fresh wallet defaults to Recipient (LUD-06-safe).
        await GoToUrl($"/plugins/ark/stores/{storeAId}/overview");
        var toggleBtn = Page!.Locator("[data-testid='toggle-fee-payer-btn']");
        Assert.Equal("Recipient pays", (await toggleBtn.TextContentAsync())?.Trim());

        // Toggle on store A ... (the redirect target is the overview page
        // itself, which holds long-polling XHRs — wait for DOMContentLoaded,
        // not NetworkIdle, or this hangs; see GoToUrl's comment above).
        await toggleBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        toggleBtn = Page.Locator("[data-testid='toggle-fee-payer-btn']");
        Assert.Equal("Sender pays", (await toggleBtn.TextContentAsync())?.Trim());

        // ... must show up on store B without touching it, since both
        // stores share the same wallet.
        await GoToUrl($"/plugins/ark/stores/{storeBId}/overview");
        toggleBtn = Page.Locator("[data-testid='toggle-fee-payer-btn']");
        Assert.Equal("Sender pays", (await toggleBtn.TextContentAsync())?.Trim());

        // Toggle back from store B ...
        await toggleBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        toggleBtn = Page.Locator("[data-testid='toggle-fee-payer-btn']");
        Assert.Equal("Recipient pays", (await toggleBtn.TextContentAsync())?.Trim());

        // ... and back on store A too.
        await GoToUrl($"/plugins/ark/stores/{storeAId}/overview");
        toggleBtn = Page.Locator("[data-testid='toggle-fee-payer-btn']");
        Assert.Equal("Recipient pays", (await toggleBtn.TextContentAsync())?.Trim());

        // Cross-check the actual persistence layer: a single wallet-level
        // metadata key, not two independent store configs.
        var walletStorage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<IWalletStorage>();
        var wallet = await walletStorage.GetWalletById(walletId!);
        var metadata = wallet?.Metadata;
        Assert.NotNull(metadata);
        var hasKey = metadata.TryGetValue(ArkLightningClient.ReverseSwapFeePayerMetadataKey, out var raw);
        Assert.True(hasKey, "wallet metadata has no ReverseSwapFeePayer key");
        Assert.Equal(nameof(ReverseSwapFeePayer.Recipient), raw);
    }
}
