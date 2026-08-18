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
