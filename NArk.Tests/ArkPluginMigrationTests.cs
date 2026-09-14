using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace NArk.Tests;

public class ArkPluginMigrationTests
{
    [Fact]
    public void ComposedSettlementShipsExactlyOneMigrationWithAnIndexNotAJournal()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var entries = assembly.Migrations.Where(m => m.Key.StartsWith("2026091", StringComparison.Ordinal)).ToArray();
        var entry = Assert.Single(entries);
        Assert.EndsWith("_AddComposedEvmSettlement", entry.Key);
        var migration = assembly.CreateMigration(entry.Value, context.Database.ProviderName!);

        var created = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("CompositionRoutes", created.Name);
        Assert.Equal(["RouteId"], created.PrimaryKey!.Columns);
        Assert.DoesNotContain(migration.UpOperations, operation =>
            operation is CreateTableOperation table &&
            (table.Name == "InvoiceCompositions" || table.Name == "InvoiceCompositionLegs"));
        var metadata = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        Assert.Equal("Metadata", metadata.Name);
        Assert.Equal("ArkadeSwapIntents", metadata.Table);
        var sql = context.GetService<IMigrator>().GenerateScript("20260814002907_AddArkadeSwapIntents", entry.Key);
        Assert.Contains("CompositionRoutes", sql);
        Assert.DoesNotContain("InvoiceCompositions", sql);
        Assert.DoesNotContain("InvoiceCompositionLegs", sql);
    }

    [Fact]
    public void PinnedSdkModelHasAMigrationBeforeServerStartup()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
