using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantTopUpFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddColumn<int>(
                name: "TopUpFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<BigInteger>(
                name: "TopUpFeeFixed",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[DepositFeeBps] >= 0 AND [DepositFeeBps] < 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000 AND [TopUpFeeBps] >= 0 AND [TopUpFeeBps] <= 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND [TopUpFeeFixed] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
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
                name: "TopUpFeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "TopUpFeeFixed",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[DepositFeeBps] >= 0 AND [DepositFeeBps] < 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
        }
    }
}
