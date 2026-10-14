using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettlementWalletApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScreenedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreeningDecision",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                // Every address on file before this change was added by platform staff. EF scaffolded '' here,
                // which the enum mapping can't read back — every merchant load would throw.
                defaultValue: "Staff");

            migrationBuilder.AddColumn<string>(
                name: "SubmittedBy",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantSettlementWallet_Status_UpdatedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MerchantSettlementWallet_Status_UpdatedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "ScreenedAt",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "ScreeningDecision",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                schema: "merchant",
                table: "MerchantSettlementWallet");
        }
    }
}
