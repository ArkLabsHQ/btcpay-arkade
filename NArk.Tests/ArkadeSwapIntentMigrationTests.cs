using System.Data.Common;
using System.Text.Json;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace NArk.Tests;

public class ArkadeSwapIntentMigrationTests
{
    private static readonly string[] TypeNames =
        ["BtcToAsset", "AssetToBtc", "BtcToLightning", "LightningToBtc", "BtcToOnchain"];

    private static readonly string[] StatusNames =
        ["Funding", "Pending", "Claimable", "Cancelling", "Fulfilled", "Cancelled", "Recoverable", "Refundable", "Resolved"];

    [Fact]
    public void UpgradePreservesLegacySecretsAndTranslatesEveryOrdinal()
    {
        using var connection = OpenLegacyDatabase();
        var migration = CreateMigration("Microsoft.EntityFrameworkCore.Sqlite");

        ExecuteOperations(connection, migration.UpOperations);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT \"Id\", \"Type\", \"Status\" FROM \"ArkadeSwapIntents\" ORDER BY \"Id\"";
            using var reader = command.ExecuteReader();
            var rows = new List<(string Id, string Type, string Status)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

            Assert.Equal(9, rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal($"intent-{i}", rows[i].Id);
                Assert.Equal(TypeNames[i % TypeNames.Length], rows[i].Type);
                Assert.Equal(StatusNames[i], rows[i].Status);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT "Metadata", "PaymentHash", "RefundLocktime", "SpentTxid"
                FROM "ArkadeSwapIntents" WHERE "Id" = 'intent-4'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0));
            Assert.NotNull(metadata);
            Assert.Equal("offer-secret", metadata["offerHex"]);
            Assert.Equal("maker-secret", metadata["makerDescriptor"]);
            Assert.Equal("invoice-secret", metadata["invoice"]);
            Assert.Equal("preimage-secret", metadata["preimage"]);
            Assert.Equal(4, metadata.Count);
            Assert.Equal("payment-history", reader.GetString(1));
            Assert.Equal(1700000100, reader.GetInt64(2));
            Assert.Equal("spent-history", reader.GetString(3));
        }

        Assert.Equal(0L, ReadScalar<long>(connection,
            "SELECT COUNT(*) FROM pragma_table_info('ArkadeSwapIntents') WHERE \"name\" IN ('OfferHex', 'Preimage', 'Invoice', 'MakerDescriptor')"));
        Assert.Equal("TEXT", ReadScalar<string>(connection,
            "SELECT \"type\" FROM pragma_table_info('ArkadeSwapIntents') WHERE \"name\" = 'Type'"));

        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO "ArkadeSwapIntents" (
                "Id", "WalletId", "Type", "OfferAmount", "WantAmount", "Status", "CreatedAt",
                "SwapPkScript", "SwapAddress", "Metadata")
            VALUES (
                'current-row', 'wallet', 'BtcToLightning', 10, 20, 'Pending',
                '2026-09-11T12:00:00Z', '0014', 'ark1', '{}')
            """;
        Assert.Equal(1, insert.ExecuteNonQuery());
    }

    [Fact]
    public void DownDropsOnlyTheRouteIndex()
    {
        using var connection = OpenLegacyDatabase();
        var migration = CreateMigration("Microsoft.EntityFrameworkCore.Sqlite");
        ExecuteOperations(connection, migration.UpOperations);
        var before = ReadScalar<string>(connection,
            "SELECT \"Metadata\" FROM \"ArkadeSwapIntents\" WHERE \"Id\" = 'intent-4'");

        ExecuteOperations(connection, migration.DownOperations);

        Assert.Single(migration.DownOperations);
        Assert.Equal("CompositionRoutes", Assert.IsType<DropTableOperation>(migration.DownOperations[0]).Name);
        Assert.Equal(before, ReadScalar<string>(connection,
            "SELECT \"Metadata\" FROM \"ArkadeSwapIntents\" WHERE \"Id\" = 'intent-4'"));
    }

    [Fact]
    public void PostgreSqlUpgradeFoldsLegacyColumnsAndTranslatesOrdinals()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var migration = CreateMigration(context.Database.ProviderName!);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var sql = string.Join("\n", generator.Generate(migration.UpOperations).Select(command => command.CommandText));

        Assert.Contains("jsonb_build_object", sql);
        Assert.Contains("'offerHex', \"OfferHex\"", sql);
        Assert.Contains("WHEN 4 THEN 'BtcToOnchain'", sql);
        Assert.Contains("WHEN 8 THEN 'Resolved'", sql);
        Assert.Contains("DROP COLUMN", sql);
        Assert.Contains("CREATE TABLE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("InvoiceCompositions", sql);
    }

    private static SqliteConnection OpenLegacyDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE "ArkadeSwapIntents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ArkadeSwapIntents" PRIMARY KEY,
                "WalletId" TEXT NOT NULL,
                "Type" INTEGER NOT NULL,
                "OfferAmount" INTEGER NOT NULL,
                "WantAmount" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "SwapPkScript" TEXT NOT NULL,
                "SwapAddress" TEXT NOT NULL,
                "OfferHex" TEXT NOT NULL,
                "MakerDescriptor" TEXT NULL,
                "FromAssetId" TEXT NULL,
                "ToAssetId" TEXT NULL,
                "Invoice" TEXT NULL,
                "PaymentHash" TEXT NULL,
                "RefundLocktime" INTEGER NULL,
                "Preimage" TEXT NULL,
                "SpentTxid" TEXT NULL
            );
            CREATE INDEX "IX_ArkadeSwapIntents_PaymentHash" ON "ArkadeSwapIntents" ("PaymentHash");
            CREATE INDEX "IX_ArkadeSwapIntents_SwapPkScript" ON "ArkadeSwapIntents" ("SwapPkScript");
            CREATE INDEX "IX_ArkadeSwapIntents_WalletId_Status" ON "ArkadeSwapIntents" ("WalletId", "Status");
            """;
        command.ExecuteNonQuery();

        for (var i = 0; i < StatusNames.Length; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO "ArkadeSwapIntents" (
                    "Id", "WalletId", "Type", "OfferAmount", "WantAmount", "Status", "CreatedAt",
                    "SwapPkScript", "SwapAddress", "OfferHex", "MakerDescriptor", "Invoice",
                    "PaymentHash", "RefundLocktime", "Preimage", "SpentTxid")
                VALUES (
                    $id, 'wallet', $type, 100, 200, $status, '2026-09-11T12:00:00Z',
                    $script, 'ark1', $offer, $maker, $invoice, $paymentHash, $refundLocktime,
                    $preimage, $spentTxid)
                """;
            insert.Parameters.AddWithValue("$id", $"intent-{i}");
            insert.Parameters.AddWithValue("$type", i % TypeNames.Length);
            insert.Parameters.AddWithValue("$status", i);
            insert.Parameters.AddWithValue("$script", $"script-{i}");
            var carriesSecrets = i == 4;
            AddNullable(insert, "$offer", carriesSecrets ? "offer-secret" : $"offer-{i}");
            AddNullable(insert, "$maker", carriesSecrets ? "maker-secret" : null);
            AddNullable(insert, "$invoice", carriesSecrets ? "invoice-secret" : null);
            AddNullable(insert, "$paymentHash", carriesSecrets ? "payment-history" : null);
            AddNullable(insert, "$refundLocktime", carriesSecrets ? 1700000100L : null);
            AddNullable(insert, "$preimage", carriesSecrets ? "preimage-secret" : null);
            AddNullable(insert, "$spentTxid", carriesSecrets ? "spent-history" : null);
            insert.ExecuteNonQuery();
        }

        return connection;
    }

    private static Migration CreateMigration(string providerName)
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(migration => migration.Key.EndsWith("_AddComposedEvmSettlement"));
        return assembly.CreateMigration(entry.Value, providerName);
    }

    private static void ExecuteOperations(SqliteConnection connection, IReadOnlyList<MigrationOperation> operations)
    {
        var options = new DbContextOptionsBuilder().UseSqlite(connection).Options;
        using var context = new DbContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        foreach (var migrationCommand in generator.Generate(operations))
        {
            using var command = connection.CreateCommand();
            command.CommandText = migrationCommand.CommandText;
            command.ExecuteNonQuery();
        }
    }

    private static T ReadScalar<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
