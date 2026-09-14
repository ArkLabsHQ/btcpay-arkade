using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Legacy;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkSdkCompatibilityTests
{
    [Fact]
    public void HistoricalSwapTableRetainsItsColumnsKeysAndForeignKeys()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!;
        var previous = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;
        var before = previous.GetRelationalModel().FindTable("Swaps", "BTCPayServer.Plugins.Ark")!;
        var after = current.GetRelationalModel().FindTable("Swaps", "BTCPayServer.Plugins.Ark")!;

        Assert.Equal(before.Columns.Select(c => (c.Name, c.StoreType, c.IsNullable)),
            after.Columns.Select(c => (c.Name, c.StoreType, c.IsNullable)));
        Assert.Equal(before.PrimaryKey!.Columns.Select(c => c.Name), after.PrimaryKey!.Columns.Select(c => c.Name));
        Assert.Equal(before.ForeignKeyConstraints.Select(f => (f.Name, f.PrincipalTable.Name)).Order(),
            after.ForeignKeyConstraints.Select(f => (f.Name, f.PrincipalTable.Name)).Order());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<LegacySwapStatus>().Select(s => (int)s));
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<LegacySwapType>().Select(s => (int)s));
    }

    [Fact]
    public void IntentStorageRequiresAndUsesCurrentSdkRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ArkPluginDbContext>>(new CompatibilityDbFactory());
        services.AddArkEfCoreStorage<ArkPluginDbContext>();
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IArkadeIntentStorage));
        services.AddArkadeEfCoreStorage();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EfCoreArkadeIntentStorage>(provider.GetRequiredService<IArkadeIntentStorage>());
    }

    [Fact]
    public void LightningPaymentReadsPersistedTypedMetadata()
    {
        var intent = new ArkadeSwapIntent
        {
            Id = "rfq", WalletId = "wallet", Type = ArkadeSwapIntentType.BtcToLightning,
            Status = ArkadeSwapIntentStatus.Fulfilled, CreatedAt = DateTimeOffset.UtcNow,
            OfferAmount = Money.Satoshis(1025), WantAmount = Money.Satoshis(1000),
            SwapPkScript = "", SwapAddress = "", PaymentHash = new string('2', 64)
        }.WithLightningMetadata(new LightningSwapMetadata("stored-invoice", new string('1', 64)));

        var payment = ArkadeIntentLightningMapper.ToPayment(intent, Network.RegTest);

        Assert.Equal("stored-invoice", payment.BOLT11);
        Assert.Equal(new string('1', 64), payment.Preimage);
        Assert.Equal(25, payment.Fee!.MilliSatoshi / 1000);
    }

    private sealed class CompatibilityDbFactory : IDbContextFactory<ArkPluginDbContext>
    {
        public ArkPluginDbContext CreateDbContext() => new DesignTimeDbContextFactory().CreateDbContext([]);
    }
}
