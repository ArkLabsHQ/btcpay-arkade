using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsNote",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                newName: "Recoverable");

            migrationBuilder.AddColumn<string>(
                name: "IntentSchedulingPolicy",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    InternalId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "IX_Intents_IntentId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Intents",
                column: "IntentId",
                unique: true,
                filter: "\"IntentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IntentVtxos_VtxoTransactionId_VtxoTransactionOutputIndex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "IntentVtxos",
                columns: new[] { "VtxoTransactionId", "VtxoTransactionOutputIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntentVtxos",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropTable(
                name: "Intents",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropColumn(
                name: "IntentSchedulingPolicy",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.RenameColumn(
                name: "Recoverable",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                newName: "IsNote");
        }
    }
}
