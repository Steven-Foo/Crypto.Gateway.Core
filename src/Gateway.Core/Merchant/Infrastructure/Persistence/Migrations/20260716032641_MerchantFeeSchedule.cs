using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantFeeSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddColumn<int>(
                name: "DepositFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<BigInteger>(
                name: "DepositFeeFixed",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<int>(
                name: "WithdrawalFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[DepositFeeBps] >= 0 AND [DepositFeeBps] < 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "DepositFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "DepositFeeFixed",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "WithdrawalFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
        }
    }
}
