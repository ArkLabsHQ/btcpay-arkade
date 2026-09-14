using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.Abstractions.Wallets;
using Xunit;

namespace NArk.Tests;

/// <summary>
/// Covers the authorization boundary the Arkade Lightning client relies on: a connection
/// string only authorises spending when it carries the capability issued for that wallet.
/// </summary>
public class ArkLightningSpendKeyServiceTests
{
    private const string WalletId = "wallet-under-test";

    private static ArkLightningSpendKeyService NewService() => new(new FakeWalletStorage());

    [Fact]
    public async Task Verify_RejectsWhenNoCapabilityHasBeenIssued()
    {
        var service = NewService();

        // Nothing issued for this wallet, so no presented value can authorise a spend.
        Assert.False(await service.VerifyAsync(WalletId, "any-value-at-all"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Verify_RejectsAbsentCapability(string? presented)
    {
        var service = NewService();
        await service.GetOrCreateAsync(WalletId);

        Assert.False(await service.VerifyAsync(WalletId, presented));
    }

    [Fact]
    public async Task Verify_RejectsWrongCapability()
    {
        var service = NewService();
        await service.GetOrCreateAsync(WalletId);

        Assert.False(await service.VerifyAsync(WalletId, "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public async Task Verify_RejectsCapabilityBelongingToAnotherWallet()
    {
        var service = NewService();
        var othersCapability = await service.GetOrCreateAsync("wallet-owned-by-someone-else");
        await service.GetOrCreateAsync(WalletId);

        // Holding a capability for one wallet must not authorise spending a different one.
        Assert.False(await service.VerifyAsync(WalletId, othersCapability));
    }

    [Fact]
    public async Task Verify_AcceptsIssuedCapability()
    {
        var service = NewService();
        var capability = await service.GetOrCreateAsync(WalletId);

        Assert.True(await service.VerifyAsync(WalletId, capability));
    }

    [Fact]
    public async Task GetOrCreate_ReturnsOneStableCapabilityPerWallet()
    {
        var service = NewService();

        // Stability is what lets an owner share one connection string across their stores.
        Assert.Equal(await service.GetOrCreateAsync(WalletId), await service.GetOrCreateAsync(WalletId));
    }

    [Fact]
    public async Task GetOrCreate_ReadsBackACapabilityIssuedBeforeThisProcess()
    {
        var storage = new FakeWalletStorage();
        var issued = await new ArkLightningSpendKeyService(storage).GetOrCreateAsync(WalletId);

        // A fresh instance shares no cache, so this exercises the storage read path.
        var afterRestart = new ArkLightningSpendKeyService(storage);

        Assert.Equal(issued, await afterRestart.GetOrCreateAsync(WalletId));
        Assert.True(await afterRestart.VerifyAsync(WalletId, issued));
    }

    [Fact]
    public async Task Regenerate_SupersedesThePreviousCapability()
    {
        var service = NewService();
        var superseded = await service.GetOrCreateAsync(WalletId);

        var current = await service.RegenerateAsync(WalletId);

        Assert.NotEqual(superseded, current);
        Assert.False(await service.VerifyAsync(WalletId, superseded));
        Assert.True(await service.VerifyAsync(WalletId, current));
    }

    [Fact]
    public async Task ConnectionString_CarriesTheCapabilityOnlyWhenBuiltForAnOwner()
    {
        var service = NewService();

        var receiveOnly = ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(WalletId);
        Assert.DoesNotContain("spend-key", receiveOnly, StringComparison.OrdinalIgnoreCase);

        var full = await service.BuildConnectionStringAsync(WalletId);
        Assert.Contains($"spend-key={await service.GetOrCreateAsync(WalletId)}", full);
        Assert.DoesNotContain("store-id", full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreScopedBuildersKeepTheSameWalletCapability()
    {
        var service = NewService();
        var capability = await service.GetOrCreateAsync(WalletId);

        Assert.Equal($"type=arkade;wallet-id={WalletId};store-id=store-A",
            ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(WalletId, "store-A"));
        Assert.Equal($"type=arkade;wallet-id={WalletId};store-id=store-A;spend-key={capability}",
            await service.BuildConnectionStringAsync(WalletId, storeId: "store-A"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackfillScopesTheExactWalletAndPreservesExistingCapability(bool owned)
    {
        var service = NewService();
        var input = $"type=arkade;wallet-id={WalletId};spend-key=existing-capability";

        var updated = await service.BackfillConnectionStringAsync(input, WalletId, "store-A", owned);

        Assert.Equal(input + ";store-id=store-A", updated);
        Assert.Equal(updated, await service.BackfillConnectionStringAsync(updated!, WalletId, "store-A", owned));
    }

    [Fact]
    public async Task BackfillNeverGrantsSpendCapabilityToReceiveOnlyWallet()
    {
        var updated = await NewService().BackfillConnectionStringAsync(
            $"type=arkade;wallet-id={WalletId}", WalletId, "store-A", false);

        Assert.Equal($"type=arkade;wallet-id={WalletId};store-id=store-A", updated);
    }

    [Fact]
    public async Task BackfillRetainsExistingOwnedWalletCapabilityUpgrade()
    {
        var service = NewService();
        var capability = await service.GetOrCreateAsync(WalletId);

        var updated = await service.BackfillConnectionStringAsync(
            $"type=arkade;wallet-id={WalletId}", WalletId, "store-A", true);

        Assert.Equal($"type=arkade;wallet-id={WalletId};store-id=store-A;spend-key={capability}", updated);
    }

    [Theory]
    [InlineData("type=arkade;wallet-id=another-wallet")]
    [InlineData("type=arkade;wallet-id=wallet-under-test;store-id=another-store")]
    [InlineData("type=arkade;wallet-id=wallet-under-test;store-id=")]
    [InlineData("type=other;wallet-id=wallet-under-test")]
    public async Task BackfillDoesNotRewriteOtherWalletsOrExplicitStoreBindings(string input)
    {
        Assert.Null(await NewService().BackfillConnectionStringAsync(input, WalletId, "store-A", true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("store;spend-key=injected")]
    [InlineData("store\nsecret")]
    public void StoreScopedBuilderRejectsInvalidIdentityWithoutEchoingInput(string storeId)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(WalletId, storeId));

        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("injected", error.Message);
    }

    /// <summary>
    /// In-memory <see cref="IWalletStorage"/> covering only the members the service uses; the
    /// rest throw so an unexpected dependency shows up as a failure rather than a silent pass.
    /// </summary>
    private sealed class FakeWalletStorage : IWalletStorage
    {
        private readonly Dictionary<string, Dictionary<string, string>> _metadata = new();

        public Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default)
            => Task.FromResult<ArkWalletInfo?>(new ArkWalletInfo(
                walletId, null, null, WalletType.SingleKey, null, 0,
                _metadata.TryGetValue(walletId, out var metadata) ? metadata : null));

        public Task SetMetadataValue(string walletId, string key, string? value, CancellationToken ct = default)
        {
            if (!_metadata.TryGetValue(walletId, out var metadata))
                _metadata[walletId] = metadata = new Dictionary<string, string>();

            if (value is null) metadata.Remove(key);
            else metadata[key] = value;

            return Task.CompletedTask;
        }

        public event EventHandler<ArkWalletInfo>? WalletSaved { add { } remove { } }
        public event EventHandler<string>? WalletDeleted { add { } remove { } }

        public Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteWallet(string walletId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
