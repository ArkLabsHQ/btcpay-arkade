using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    [DbContext(typeof(ArkPluginDbContext))]
    [Migration("20260114220000_ContractActivityState")]
    public partial class ContractActivityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new ActivityState column (integer)
            migrationBuilder.AddColumn<int>(
                name: "ActivityState",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Migrate data from Active (bool) to ActivityState (int)
            // Active = true  -> ActivityState = 1 (Active)
            // Active = false -> ActivityState = 0 (Inactive)
            migrationBuilder.Sql(
                @"UPDATE ""BTCPayServer.Plugins.Ark"".""WalletContracts""
                  SET ""ActivityState"" = CASE WHEN ""Active"" = true THEN 1 ELSE 0 END");

            // Drop the old Active column
            migrationBuilder.DropColumn(
                name: "Active",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add back the Active column
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Migrate data from ActivityState (int) back to Active (bool)
            // ActivityState = 0 (Inactive) -> Active = false
            // ActivityState > 0 (Active or AwaitingFunds) -> Active = true
            migrationBuilder.Sql(
                @"UPDATE ""BTCPayServer.Plugins.Ark"".""WalletContracts""
                  SET ""Active"" = CASE WHEN ""ActivityState"" > 0 THEN true ELSE false END");

            // Drop the ActivityState column
            migrationBuilder.DropColumn(
                name: "ActivityState",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");
        }
    }
}
