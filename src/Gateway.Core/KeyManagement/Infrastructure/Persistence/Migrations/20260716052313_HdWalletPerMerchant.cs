using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HdWalletPerMerchant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HdWallet_Chain_Purpose",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "keymgmt",
                table: "HdWallet",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HdWallet_MerchantId_Chain_Purpose",
                schema: "keymgmt",
                table: "HdWallet",
                columns: new[] { "MerchantId", "Chain", "Purpose" },
                unique: true,
                filter: "[Status] = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HdWallet_MerchantId_Chain_Purpose",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.CreateIndex(
                name: "IX_HdWallet_Chain_Purpose",
                schema: "keymgmt",
                table: "HdWallet",
                columns: new[] { "Chain", "Purpose" },
                unique: true,
                filter: "[Status] = 'Active'");
        }
    }
}
