using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantApprovalThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<BigInteger>(
                name: "ApprovalThreshold",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_ApprovalThreshold",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[ApprovalThreshold] IS NULL OR [ApprovalThreshold] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_ApprovalThreshold",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "ApprovalThreshold",
                schema: "merchant",
                table: "MerchantAssetPolicy");
        }
    }
}
