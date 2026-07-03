using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New required column. The scaffolder defaults existing rows to 0 (= no scope flag),
            // which fails the bitwise include filter (Scope & Offchain) == Offchain and would drop
            // every pre-existing contract out of off-chain sync/recovery. Default to Offchain (2).
            migrationBuilder.AddColumn<int>(
                name: "Scope",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            // Boarding contracts live on-chain as boarding UTXOs (ContractScope.Onchain = 1).
            migrationBuilder.Sql(
                "UPDATE \"BTCPayServer.Plugins.Ark\".\"WalletContracts\" SET \"Scope\" = 1 WHERE \"Type\" = 'Boarding';");

            // Drop the backfill default so the column matches the model (entity config declares no
            // default); EF writes Scope from each contract's DefaultScope on insert.
            migrationBuilder.Sql(
                "ALTER TABLE \"BTCPayServer.Plugins.Ark\".\"WalletContracts\" ALTER COLUMN \"Scope\" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_WalletContracts_Scope",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts",
                column: "Scope");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletContracts_Scope",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "BTCPayServer.Plugins.Ark",
                table: "WalletContracts");
        }
    }
}
