using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArkadeSwapIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArkadeSwapIntents",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    OfferAmount = table.Column<long>(type: "bigint", nullable: false),
                    WantAmount = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SwapPkScript = table.Column<string>(type: "text", nullable: false),
                    SwapAddress = table.Column<string>(type: "text", nullable: false),
                    OfferHex = table.Column<string>(type: "text", nullable: false),
                    MakerDescriptor = table.Column<string>(type: "text", nullable: true),
                    FromAssetId = table.Column<string>(type: "text", nullable: true),
                    ToAssetId = table.Column<string>(type: "text", nullable: true),
                    Invoice = table.Column<string>(type: "text", nullable: true),
                    PaymentHash = table.Column<string>(type: "text", nullable: true),
                    RefundLocktime = table.Column<long>(type: "bigint", nullable: true),
                    Preimage = table.Column<string>(type: "text", nullable: true),
                    SpentTxid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArkadeSwapIntents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArkadeSwapIntents_PaymentHash",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                column: "PaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_ArkadeSwapIntents_SwapPkScript",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                column: "SwapPkScript");

            migrationBuilder.CreateIndex(
                name: "IX_ArkadeSwapIntents_WalletId_Status",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                columns: new[] { "WalletId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArkadeSwapIntents",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
