using System.Globalization;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNullableWithdrawalMinimum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_WithdrawalRange",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AlterColumn<BigInteger>(
                name: "MinimumWithdrawal",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: true,
                oldClrType: typeof(BigInteger),
                oldType: "decimal(38,0)");

            // MinimumWithdrawal is now a per-merchant OVERRIDE where NULL = "use the platform config minimum".
            // Every existing 0 was the old non-null default (nothing ever set it deliberately), i.e. "unset" —
            // migrate those to NULL so they fall back to config rather than silently forcing a 0 minimum.
            migrationBuilder.Sql(
                "UPDATE [merchant].[MerchantAssetPolicy] SET [MinimumWithdrawal] = NULL WHERE [MinimumWithdrawal] = 0;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_WithdrawalRange",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[MaximumWithdrawal] IS NULL OR [MinimumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_WithdrawalRange",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            // Restore the non-null column: backfill any unset (NULL) minimum to 0 first so the NOT NULL alter succeeds.
            migrationBuilder.Sql(
                "UPDATE [merchant].[MerchantAssetPolicy] SET [MinimumWithdrawal] = 0 WHERE [MinimumWithdrawal] IS NULL;");

            migrationBuilder.AlterColumn<BigInteger>(
                name: "MinimumWithdrawal",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: false,
                defaultValue: BigInteger.Parse("0", NumberFormatInfo.InvariantInfo),
                oldClrType: typeof(BigInteger),
                oldType: "decimal(38,0)",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_NonNegative",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_WithdrawalRange",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]");
        }
    }
}
