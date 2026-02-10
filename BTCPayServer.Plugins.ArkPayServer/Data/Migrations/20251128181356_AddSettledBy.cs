using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettledBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettledByTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettledByTransactionId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");
        }
    }
}
