using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
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
    /// Happy path: fund store A, send a portion to store B's Arkade
    /// address, assert B's balance reflects the inbound VTXO.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SendToArkAddress_RecipientReceivesVtxo()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        // Sender: fund 100k sats.
        var senderStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var senderWalletId = await GetStoreWalletIdAsync(senderStoreId);
        await FundWalletViaNoteAsync(senderWalletId!, 100_000);
        await PollForBalanceAsync(senderStoreId, (long)(100_000 * 0.97));

        // Recipient: nsec store so it exposes a stable Arkade address.
        var recipientStoreId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");
        Assert.False(string.IsNullOrWhiteSpace(recipientAddr));

        // Select coins for a 40k transfer.
        const long sendSats = 40_000;
        var outpoints = await SuggestOutpointsAsync(senderStoreId, sendSats);
        Assert.NotEmpty(outpoints);

        // Submit the spend through the build-intent form.
        await GoToUrl($"/plugins/ark/stores/{senderStoreId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{senderStoreId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = UrlEncodeForm(new()
                {
                    ["StoreId"] = senderStoreId,
                    ["VtxoOutpointsRaw"] = string.Join(",", outpoints),
                    ["Outputs[0].Destination"] = recipientAddr,
                    ["Outputs[0].AmountBtc"] = (sendSats / 100_000_000m)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
            });

        // build-intent redirects (302) to overview on success; APIRequest
        // follows it, so a 2xx final status is the success signal.
        Assert.True(resp.Ok, $"build-intent returned {resp.Status}");

        // Recipient's VTXO lands after the batch the intent joined.
        var recipientBalance = await PollForBalanceAsync(
            recipientStoreId, (long)(sendSats * 0.95), TimeSpan.FromMinutes(3));
        Assert.True(recipientBalance >= (long)(sendSats * 0.95),
            $"recipient balance {recipientBalance} never reflected the {sendSats} send");
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

    private async Task<List<string>> SuggestOutpointsAsync(string storeId, long amountSats)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/suggest-coins").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destinationType = "ArkAddress", amountSats }
            });

        var raw = await resp.TextAsync();
        if (!resp.Ok)
            throw new InvalidOperationException($"suggest-coins returned {resp.Status}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.GetString() is { Length: > 0 } msg)
            throw new InvalidOperationException($"suggest-coins error: {msg}");
        if (!root.TryGetProperty("suggestedOutpoints", out var op) ||
            op.ValueKind != JsonValueKind.Array)
            return [];
        return op.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
    }

    private async Task FundWalletViaNoteAsync(string walletId, long amountSats)
    {
        var note = await CreateArkNoteAsync(amountSats);
        Assert.False(string.IsNullOrEmpty(note), "arkd note CLI returned empty");
        var sp = _fixture.ServerTester!.PayTester.ServiceProvider;
        var contractService = sp.GetRequiredService<IContractService>();
        await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
    }

    private async Task<string?> GetStoreWalletIdAsync(string storeId)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        return await Page.GetAttributeAsync(".truncate-center-id", "data-text");
    }

    private async Task<long> PollForBalanceAsync(string storeId, long minSats, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        long last = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
            last = await ReadAvailableBalanceSatsAsync();
            if (last >= minSats) return last;
            await Task.Delay(3_000);
        }
        throw new TimeoutException(
            $"Wallet {storeId} balance never reached {minSats} sats (last: {last}).");
    }

    private async Task<long> ReadAvailableBalanceSatsAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);
        var locator = Page.Locator("[data-testid='wallet-balance']").First;
        if (await locator.CountAsync() == 0) return 0;
        var text = await locator.InnerTextAsync();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:[.,]\d+)?");
        if (!match.Success) return 0;
        if (!decimal.TryParse(
                match.Value.Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var btc)) return 0;
        return (long)(btc * 100_000_000m);
    }

    private static string GenerateRandomNsec()
    {
        Span<byte> keyBytes = stackalloc byte[32];
        Random.Shared.NextBytes(keyBytes);
        if (!ECPrivKey.TryCreate(keyBytes, out _))
        {
            keyBytes.Clear();
            keyBytes[31] = 0x01;
        }
        var encoder = Encoders.Bech32("nsec");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(keyBytes.ToArray(), Bech32EncodingType.BECH32);
    }
}
