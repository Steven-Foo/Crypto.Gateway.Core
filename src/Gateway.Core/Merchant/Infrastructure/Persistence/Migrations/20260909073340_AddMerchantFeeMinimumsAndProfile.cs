using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantFeeMinimumsAndProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddColumn<BigInteger>(
                name: "MaximumDeposit",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MinimumDeposit",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MinimumDepositFee",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<BigInteger>(
                name: "MinimumWithdrawalFee",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "merchant",
                table: "Merchant",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remark",
                schema: "merchant",
                table: "Merchant",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementMode",
                schema: "merchant",
                table: "Merchant",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValueSql: "'Manual'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_DepositRange",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[MaximumDeposit] IS NULL OR [MinimumDeposit] IS NULL OR [MaximumDeposit] >= [MinimumDeposit]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND [TopUpFeeFixed] >= 0 AND [MinimumDepositFee] >= 0 AND [MinimumWithdrawalFee] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0) AND ([MinimumDeposit] IS NULL OR [MinimumDeposit] >= 0) AND ([MaximumDeposit] IS NULL OR [MaximumDeposit] >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_DepositRange",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MaximumDeposit",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MinimumDeposit",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MinimumDepositFee",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MinimumWithdrawalFee",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "merchant",
                table: "Merchant");

            migrationBuilder.DropColumn(
                name: "Remark",
                schema: "merchant",
                table: "Merchant");

            migrationBuilder.DropColumn(
                name: "SettlementMode",
                schema: "merchant",
                table: "Merchant");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND [TopUpFeeFixed] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
        }
    }
}
