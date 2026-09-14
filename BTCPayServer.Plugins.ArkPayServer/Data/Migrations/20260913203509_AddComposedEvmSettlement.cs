using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <summary>
    /// Single squashed migration for composed EVM settlement. Swap state lives in the
    /// SDK's ArkadeSwapIntents storage: legacy per-corridor columns are folded into the
    /// Metadata JSON document and the int enums become their member names. The plugin
    /// keeps only a CompositionRoutes index (invoice to SDK swap ids); quote, funding,
    /// proof and lifecycle state are read live from SDK intent storage, never duplicated.
    /// </summary>
    public partial class AddComposedEvmSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql(
                    """
                    UPDATE "ArkadeSwapIntents"
                    SET "Metadata" = (
                        SELECT json_group_object("key", "value")
                        FROM (
                            SELECT 'offerHex' AS "key", "OfferHex" AS "value"
                            UNION ALL SELECT 'makerDescriptor', "MakerDescriptor"
                            UNION ALL SELECT 'invoice', "Invoice"
                            UNION ALL SELECT 'preimage', "Preimage"
                        ) AS "LegacyMetadata"
                        WHERE "value" IS NOT NULL
                    );

                    CREATE TABLE "__temp_ArkadeSwapIntents" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ArkadeSwapIntents" PRIMARY KEY,
                        "WalletId" TEXT NOT NULL,
                        "Type" TEXT NOT NULL,
                        "OfferAmount" INTEGER NOT NULL,
                        "WantAmount" INTEGER NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "SwapPkScript" TEXT NOT NULL,
                        "SwapAddress" TEXT NOT NULL,
                        "FromAssetId" TEXT NULL,
                        "ToAssetId" TEXT NULL,
                        "PaymentHash" TEXT NULL,
                        "RefundLocktime" INTEGER NULL,
                        "SpentTxid" TEXT NULL,
                        "Metadata" TEXT NOT NULL DEFAULT '{}'
                    );

                    INSERT INTO "__temp_ArkadeSwapIntents" (
                        "Id", "WalletId", "Type", "OfferAmount", "WantAmount", "Status",
                        "CreatedAt", "SwapPkScript", "SwapAddress",
                        "FromAssetId", "ToAssetId", "PaymentHash", "RefundLocktime",
                        "SpentTxid", "Metadata")
                    SELECT
                        "Id", "WalletId",
                        CASE "Type"
                            WHEN 0 THEN 'BtcToAsset'
                            WHEN 1 THEN 'AssetToBtc'
                            WHEN 2 THEN 'BtcToLightning'
                            WHEN 3 THEN 'LightningToBtc'
                            WHEN 4 THEN 'BtcToOnchain'
                            ELSE CAST("Type" AS TEXT)
                        END,
                        "OfferAmount", "WantAmount",
                        CASE "Status"
                            WHEN 0 THEN 'Funding'
                            WHEN 1 THEN 'Pending'
                            WHEN 2 THEN 'Claimable'
                            WHEN 3 THEN 'Cancelling'
                            WHEN 4 THEN 'Fulfilled'
                            WHEN 5 THEN 'Cancelled'
                            WHEN 6 THEN 'Recoverable'
                            WHEN 7 THEN 'Refundable'
                            WHEN 8 THEN 'Resolved'
                            ELSE CAST("Status" AS TEXT)
                        END,
                        "CreatedAt", "SwapPkScript", "SwapAddress",
                        "FromAssetId", "ToAssetId", "PaymentHash", "RefundLocktime",
                        "SpentTxid", "Metadata"
                    FROM "ArkadeSwapIntents";

                    DROP TABLE "ArkadeSwapIntents";
                    ALTER TABLE "__temp_ArkadeSwapIntents" RENAME TO "ArkadeSwapIntents";
                    CREATE INDEX "IX_ArkadeSwapIntents_PaymentHash" ON "ArkadeSwapIntents" ("PaymentHash");
                    CREATE INDEX "IX_ArkadeSwapIntents_SwapPkScript" ON "ArkadeSwapIntents" ("SwapPkScript");
                    CREATE INDEX "IX_ArkadeSwapIntents_WalletId_Status" ON "ArkadeSwapIntents" ("WalletId", "Status");
                    """);
            }
            else
            {
                migrationBuilder.Sql(
                    """
                    UPDATE "BTCPayServer.Plugins.Ark"."ArkadeSwapIntents"
                    SET "Metadata" = jsonb_strip_nulls(jsonb_build_object(
                        'offerHex', "OfferHex",
                        'makerDescriptor', "MakerDescriptor",
                        'invoice', "Invoice",
                        'preimage', "Preimage"
                    ))::text;

                    ALTER TABLE "BTCPayServer.Plugins.Ark"."ArkadeSwapIntents"
                    ALTER COLUMN "Type" TYPE character varying(32) USING CASE "Type"
                        WHEN 0 THEN 'BtcToAsset'
                        WHEN 1 THEN 'AssetToBtc'
                        WHEN 2 THEN 'BtcToLightning'
                        WHEN 3 THEN 'LightningToBtc'
                        WHEN 4 THEN 'BtcToOnchain'
                        ELSE "Type"::text
                    END,
                    ALTER COLUMN "Status" TYPE character varying(32) USING CASE "Status"
                        WHEN 0 THEN 'Funding'
                        WHEN 1 THEN 'Pending'
                        WHEN 2 THEN 'Claimable'
                        WHEN 3 THEN 'Cancelling'
                        WHEN 4 THEN 'Fulfilled'
                        WHEN 5 THEN 'Cancelled'
                        WHEN 6 THEN 'Recoverable'
                        WHEN 7 THEN 'Refundable'
                        WHEN 8 THEN 'Resolved'
                        ELSE "Status"::text
                    END;
                    """);

                migrationBuilder.DropColumn(
                    name: "OfferHex",
                    schema: "BTCPayServer.Plugins.Ark",
                    table: "ArkadeSwapIntents");

                migrationBuilder.DropColumn(
                    name: "MakerDescriptor",
                    schema: "BTCPayServer.Plugins.Ark",
                    table: "ArkadeSwapIntents");

                migrationBuilder.DropColumn(
                    name: "Invoice",
                    schema: "BTCPayServer.Plugins.Ark",
                    table: "ArkadeSwapIntents");

                migrationBuilder.DropColumn(
                    name: "Preimage",
                    schema: "BTCPayServer.Plugins.Ark",
                    table: "ArkadeSwapIntents");
            }

            migrationBuilder.CreateTable(
                name: "CompositionRoutes",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    InvoiceId = table.Column<string>(type: "text", nullable: true),
                    PaymentMethodId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    OutgoingSwapId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IngressSwapId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseAmountSats = table.Column<long>(type: "bigint", nullable: false),
                    CustomerDestination = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    CheckoutExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompositionRoutes", x => x.RouteId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompositionRoutes_OutgoingSwapId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "CompositionRoutes",
                column: "OutgoingSwapId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompositionRoutes_PaymentHash",
                schema: "BTCPayServer.Plugins.Ark",
                table: "CompositionRoutes",
                column: "PaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_CompositionRoutes_StoreId_InvoiceId_PaymentMethodId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "CompositionRoutes",
                columns: new[] { "StoreId", "InvoiceId", "PaymentMethodId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversing the string enums or deleting copied metadata would destroy post-upgrade data.
            migrationBuilder.DropTable(
                name: "CompositionRoutes",
                schema: "BTCPayServer.Plugins.Ark");
        }
    }
}
