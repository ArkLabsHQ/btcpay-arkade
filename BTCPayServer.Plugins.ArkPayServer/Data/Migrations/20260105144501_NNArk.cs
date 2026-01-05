using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class NNArk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "text",
                nullable: true);

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

            migrationBuilder.AddColumn<long>(
                name: "ExpiresAtHeight",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "bigint",
                nullable: true);

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

            migrationBuilder.AddColumn<string>(
                name: "SignerDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Intents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.DropColumn(
                name: "ExpiresAtHeight",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");

            migrationBuilder.DropColumn(
                name: "Address",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps");

            migrationBuilder.DropColumn(
                name: "FailReason",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps");

            migrationBuilder.DropColumn(
                name: "SignerDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Intents");
        }
    }
}
