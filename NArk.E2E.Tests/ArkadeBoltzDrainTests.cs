using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// The migration that gets money out of Boltz swaps nobody is resolving any more.
/// </summary>
/// <remarks>
/// Seeded straight into swap storage, because nothing creates a Boltz swap any more — the corridors
/// replaced it. That is the point of the service and the reason it cannot be exercised end to end:
/// the only deployments with these rows upgraded into them, and this suite starts clean every time.
/// </remarks>
[Collection("Arkade Plugin Tests")]
[Trait("Category", "Integration")]
public class ArkadeBoltzDrainTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ArkadeBoltzDrainTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>A chain swap's onchain exposure is reported, not quietly counted as handled.</summary>
    /// <remarks>
    /// The case the service exists to make visible. Its sats sit in a Bitcoin HTLC that the sweeper
    /// cannot see — it works on Arkade coins — so the only useful thing to do with it is say so. A
    /// merchant who is told can go and get the money; one who is not, never learns it is there.
    /// </remarks>
    [Fact]
    public async Task ChainSwapWithOnchainLockup_IsReportedAsNeedingAnOperator()
    {
        var (storage, drain, walletId) = await SetUpAsync();
        var script = await SeedContractAsync(walletId);

        await storage.SaveSwap(walletId, StrandedChainSwap(walletId, script));

        var found = await drain.DrainAsync();

        var swap = Assert.Single(found, s => s.WalletId == walletId);
        Assert.Equal(SwapRecourse.OnchainNeedsOperator, swap.Recourse);
        Assert.Equal(50_000, swap.AmountSats);
    }

    /// <summary>A swap holding nothing is not reported as money to chase.</summary>
    /// <remarks>
    /// Rows outlive the sats: a swap that settled or was refunded without its status being written
    /// back looks identical to one still holding funds, until its coins are checked. Counting those
    /// as stranded would bury the ones that matter.
    /// </remarks>
    [Fact]
    public async Task SwapWithNoCoinsLeft_IsNotReportedAsStranded()
    {
        var (storage, drain, walletId) = await SetUpAsync();
        var script = await SeedContractAsync(walletId);

        await storage.SaveSwap(walletId, EmptySubmarineSwap(walletId, script));

        var found = await drain.DrainAsync();

        var swap = Assert.Single(found, s => s.WalletId == walletId);
        Assert.Equal(SwapRecourse.NothingLeft, swap.Recourse);
    }

    /// <summary>A settled swap is not examined at all.</summary>
    /// <remarks>
    /// The filter is what keeps a pass proportional to the work outstanding rather than to how much
    /// history a wallet has. A long-lived store has far more finished swaps than stuck ones.
    /// </remarks>
    [Fact]
    public async Task SettledSwap_IsLeftAlone()
    {
        var (storage, drain, walletId) = await SetUpAsync();
        var script = await SeedContractAsync(walletId);

        await storage.SaveSwap(walletId, EmptySubmarineSwap(walletId, script) with { Status = ArkSwapStatus.Settled });

        var found = await drain.DrainAsync();

        Assert.DoesNotContain(found, s => s.WalletId == walletId);
    }

    /// <summary>
    /// Builds the service over the running BTCPay's storages.
    /// </summary>
    /// <remarks>
    /// Constructed rather than resolved. Plugins load in their own AssemblyLoadContext, so the type
    /// BTCPay registered and the one this assembly compiled against are different types and the
    /// container will not hand one to the other — every other test here resolves NArk types for the
    /// same reason. Its DI wiring is proven by BTCPay starting at all; what is worth testing is the
    /// classification, and that needs nothing but the three storages.
    /// </remarks>
    private async Task<(ISwapStorage Storage, ArkadeBoltzDrainService Drain, string WalletId)> SetUpAsync()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        // A real wallet, because a swap row is foreign-keyed to one of its contracts.
        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrEmpty(walletId));

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        _contractStorage = services.GetRequiredService<IContractStorage>();
        var swapStorage = services.GetRequiredService<ISwapStorage>();

        var drain = new ArkadeBoltzDrainService(
            swapStorage,
            _contractStorage,
            services.GetRequiredService<IVtxoStorage>(),
            NullLogger<ArkadeBoltzDrainService>.Instance);

        return (swapStorage, drain, walletId!);
    }

    private IContractStorage? _contractStorage;

    /// <summary>Saves a VHTLC contract row for a swap to hang off, and returns its script.</summary>
    private async Task<string> SeedContractAsync(string walletId)
    {
        var script = RandomScript();

        await _contractStorage!.SaveContract(new ArkContractEntity(
            Script: script,
            ActivityState: ContractActivityState.Active,
            Type: "VHTLC",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: walletId,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-30)));

        return script;
    }

    /// <summary>A chain swap carrying the metadata that marks an onchain lockup.</summary>
    private static ArkSwap StrandedChainSwap(string walletId, string script) => new(
        SwapId: Guid.NewGuid().ToString(),
        WalletId: walletId,
        SwapType: ArkSwapType.ChainBtcToArk,
        Invoice: "",
        ExpectedAmount: 50_000,
        ContractScript: script,
        Address: "bcrt1qexample",
        Status: ArkSwapStatus.Pending,
        FailReason: null,
        CreatedAt: DateTimeOffset.UtcNow.AddDays(-30),
        UpdatedAt: DateTimeOffset.UtcNow.AddDays(-30),
        Hash: Guid.NewGuid().ToString("N"))
    {
        Metadata = new Dictionary<string, string> { [SwapMetadata.BtcAddress] = "bcrt1qexample" }
    };

    /// <summary>A submarine swap whose script holds no coins.</summary>
    private static ArkSwap EmptySubmarineSwap(string walletId, string script) => new(
        SwapId: Guid.NewGuid().ToString(),
        WalletId: walletId,
        SwapType: ArkSwapType.Submarine,
        Invoice: "",
        ExpectedAmount: 20_000,
        ContractScript: script,
        Address: "ark1qexample",
        Status: ArkSwapStatus.Pending,
        FailReason: null,
        CreatedAt: DateTimeOffset.UtcNow.AddDays(-30),
        UpdatedAt: DateTimeOffset.UtcNow.AddDays(-30),
        Hash: Guid.NewGuid().ToString("N"));

    /// <summary>A script nothing else in the suite will collide with.</summary>
    private static string RandomScript() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
}
