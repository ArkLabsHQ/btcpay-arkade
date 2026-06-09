using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AbsorbNNarkModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wallets_Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.AlterColumn<string>(
                name: "Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                column: "Wallet",
                unique: true,
                filter: "\"Wallet\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wallets_Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets");

            migrationBuilder.AlterColumn<string>(
                name: "Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Wallet",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                column: "Wallet",
                unique: true);
        }
    }
}
