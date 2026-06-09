using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Exercises the offchain spend path: a funded wallet selects coins via
/// /suggest-coins and submits a transfer via /build-intent to another
/// store's Arkade address. Both wallets are funded/observed through the
/// in-process IContractService note path (see FundedWalletTests).
/// </summary>
[Collection("Arkade Plugin Tests")]
public class SpendingTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public SpendingTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Unhappy path: build-intent with no VTXOs selected must surface a
    /// validation error and not move funds.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildIntent_NoCoinsSelected_ShowsError()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = UrlEncodeForm(new()
                {
                    ["StoreId"] = storeId,
                    ["VtxoOutpointsRaw"] = "",
                    ["Outputs[0].Destination"] = recipientAddr,
                    ["Outputs[0].AmountBtc"] = "0.0001"
                })
            });

        Assert.True(resp.Ok, $"build-intent returned {resp.Status}");
        var html = await resp.TextAsync();
        Assert.Contains("No valid VTXOs selected", html);
    }

    /// <summary>
    /// /suggest-coins on an empty wallet returns the "no spendable coins"
    /// error rather than throwing.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SuggestCoins_EmptyWallet_ReturnsNoCoinsError()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/suggest-coins").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destinationType = "ArkAddress", amountSats = 10_000L }
            });

        Assert.True(resp.Ok, $"suggest-coins returned {resp.Status}");
        var json = await resp.JsonAsync();
        var error = json!.Value.TryGetProperty("error", out var e) ? e.GetString() : null;
        Assert.False(string.IsNullOrEmpty(error), "empty wallet should report no spendable coins");
    }

    // --- helpers ---

    private static string UrlEncodeForm(Dictionary<string, string> fields) =>
        string.Join("&", fields.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}
