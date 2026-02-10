using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Arkade.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntentsRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // === Wallets table changes ===
            // Add new HD wallet columns
            migrationBuilder.AddColumn<string>(
                name: "AccountDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: true,
                defaultValue: "TODO_MIGRATION");

            migrationBuilder.AddColumn<int>(
                name: "LastUsedIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WalletType",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Drop unused IntentSchedulingPolicy
            migrationBuilder.DropColumn(
                name: "IntentSchedulingPolicy",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            // === Vtxos table changes ===
            migrationBuilder.AddColumn<long>(
                name: "ExpiresAtHeight",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Preconfirmed",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Unrolled",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CommitmentTxids",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArkTxid",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "text",
                nullable: true);

            // === Swaps table changes ===
            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailReason",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps",
                type: "text",
                nullable: true);

            // === WalletContracts: Active -> ActivityState migration ===
            migrationBuilder.AddColumn<int>(
                name: "ActivityState",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Migrate data: Active = true -> ActivityState = 1, Active = false -> ActivityState = 0
            migrationBuilder.Sql(
                @"UPDATE ""BTCPayServer.Plugins.Ark"".""WalletContracts""
                  SET ""ActivityState"" = CASE WHEN ""Active"" = true THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "Active",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "jsonb",
                nullable: true);

            // === Intents tables: drop and recreate with new schema ===
            // Drop IntentVtxos first (has FK to Intents)
            migrationBuilder.DropTable(
                name: "IntentVtxos",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark");

            // Recreate Intents table with IntentTxId as primary key
            migrationBuilder.CreateTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    IntentTxId = table.Column<string>(type: "text", nullable: false),
                    IntentId = table.Column<string>(type: "text", nullable: true),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisterProof = table.Column<string>(type: "text", nullable: false),
                    RegisterProofMessage = table.Column<string>(type: "text", nullable: false),
                    DeleteProof = table.Column<string>(type: "text", nullable: false),
                    DeleteProofMessage = table.Column<string>(type: "text", nullable: false),
                    BatchId = table.Column<string>(type: "text", nullable: true),
                    CommitmentTransactionId = table.Column<string>(type: "text", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    PartialForfeits = table.Column<string[]>(type: "text[]", nullable: false),
                    SignerDescriptor = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Intents", x => x.IntentTxId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Intents_IntentId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Intents",
                column: "IntentId",
                unique: true,
                filter: "\"IntentId\" IS NOT NULL");

            // Recreate IntentVtxos table with IntentTxId FK
            migrationBuilder.CreateTable(
                name: "IntentVtxos",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    IntentTxId = table.Column<string>(type: "text", nullable: false),
                    VtxoTransactionId = table.Column<string>(type: "text", nullable: false),
                    VtxoTransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntentVtxos", x => new { x.IntentTxId, x.VtxoTransactionId, x.VtxoTransactionOutputIndex });
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Intents_IntentTxId",
                        column: x => x.IntentTxId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Intents",
                        principalColumn: "IntentTxId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Vtxos_VtxoTransactionId_VtxoTransactionOutputIn~",
                        columns: x => new { x.VtxoTransactionId, x.VtxoTransactionOutputIndex },
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Vtxos",
                        principalColumns: new[] { "TransactionId", "TransactionOutputIndex" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntentVtxos_VtxoTransactionId_VtxoTransactionOutputIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "IntentVtxos",
                columns: new[] { "VtxoTransactionId", "VtxoTransactionOutputIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // === Intents tables: restore old schema ===
            migrationBuilder.DropTable(
                name: "IntentVtxos",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark");

            // Recreate old Intents table with InternalId
            migrationBuilder.CreateTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    InternalId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntentId = table.Column<string>(type: "text", nullable: true),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisterProof = table.Column<string>(type: "text", nullable: false),
                    RegisterProofMessage = table.Column<string>(type: "text", nullable: false),
                    DeleteProof = table.Column<string>(type: "text", nullable: false),
                    DeleteProofMessage = table.Column<string>(type: "text", nullable: false),
                    BatchId = table.Column<string>(type: "text", nullable: true),
                    CommitmentTransactionId = table.Column<string>(type: "text", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    PartialForfeits = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Intents", x => x.InternalId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Intents_IntentId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Intents",
                column: "IntentId",
                unique: true,
                filter: "\"IntentId\" IS NOT NULL");

            // Recreate old IntentVtxos table with InternalId FK
            migrationBuilder.CreateTable(
                name: "IntentVtxos",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    InternalId = table.Column<int>(type: "integer", nullable: false),
                    VtxoTransactionId = table.Column<string>(type: "text", nullable: false),
                    VtxoTransactionOutputIndex = table.Column<int>(type: "integer", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntentVtxos", x => new { x.InternalId, x.VtxoTransactionId, x.VtxoTransactionOutputIndex });
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Intents_InternalId",
                        column: x => x.InternalId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Intents",
                        principalColumn: "InternalId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntentVtxos_Vtxos_VtxoTransactionId_VtxoTransactionOutputIn~",
                        columns: x => new { x.VtxoTransactionId, x.VtxoTransactionOutputIndex },
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Vtxos",
                        principalColumns: new[] { "TransactionId", "TransactionOutputIndex" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntentVtxos_VtxoTransactionId_VtxoTransactionOutputIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "IntentVtxos",
                columns: new[] { "VtxoTransactionId", "VtxoTransactionOutputIndex" });

            // === WalletContracts: ActivityState -> Active ===
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE ""BTCPayServer.Plugins.Ark"".""WalletContracts""
                  SET ""Active"" = CASE WHEN ""ActivityState"" > 0 THEN true ELSE false END");

            migrationBuilder.DropColumn(
                name: "ActivityState",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropColumn(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            // === Swaps: remove columns ===
            migrationBuilder.DropColumn(
                name: "Address",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps");

            migrationBuilder.DropColumn(
                name: "FailReason",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps");

            // === Vtxos: remove columns ===
            migrationBuilder.DropColumn(
                name: "ExpiresAtHeight",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "Preconfirmed",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "Unrolled",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "CommitmentTxids",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "ArkTxid",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            // === Wallets: restore IntentSchedulingPolicy, remove HD columns ===
            migrationBuilder.AddColumn<string>(
                name: "IntentSchedulingPolicy",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.DropColumn(
                name: "AccountDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "LastUsedIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "WalletType",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");
        }
    }
}
