using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHDWalletSupport : Migration
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
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletType",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<long>(
                name: "ExpiresAtHeight",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "bigint",
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

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ExpiresAt",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
