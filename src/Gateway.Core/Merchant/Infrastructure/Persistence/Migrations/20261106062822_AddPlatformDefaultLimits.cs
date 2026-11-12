using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformDefaultLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<BigInteger>(
                name: "ApprovalThreshold",
                schema: "merchant",
                table: "DefaultFeePolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MaximumDeposit",
                schema: "merchant",
                table: "DefaultFeePolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MaximumWithdrawal",
                schema: "merchant",
                table: "DefaultFeePolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MinimumDeposit",
                schema: "merchant",
                table: "DefaultFeePolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<BigInteger>(
                name: "MinimumWithdrawal",
                schema: "merchant",
                table: "DefaultFeePolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DefaultFeePolicy_Limits",
                schema: "merchant",
                table: "DefaultFeePolicy",
                sql: "([MinimumDeposit] IS NULL OR [MinimumDeposit] >= 0) AND ([MaximumDeposit] IS NULL OR [MaximumDeposit] >= 0) AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0) AND ([ApprovalThreshold] IS NULL OR [ApprovalThreshold] >= 0) AND ([MinimumDeposit] IS NULL OR [MaximumDeposit] IS NULL OR [MinimumDeposit] <= [MaximumDeposit]) AND ([MinimumWithdrawal] IS NULL OR [MaximumWithdrawal] IS NULL OR [MinimumWithdrawal] <= [MaximumWithdrawal])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DefaultFeePolicy_Limits",
                schema: "merchant",
                table: "DefaultFeePolicy");

            migrationBuilder.DropColumn(
                name: "ApprovalThreshold",
                schema: "merchant",
                table: "DefaultFeePolicy");

            migrationBuilder.DropColumn(
                name: "MaximumDeposit",
                schema: "merchant",
                table: "DefaultFeePolicy");

            migrationBuilder.DropColumn(
                name: "MaximumWithdrawal",
                schema: "merchant",
                table: "DefaultFeePolicy");

            migrationBuilder.DropColumn(
                name: "MinimumDeposit",
                schema: "merchant",
                table: "DefaultFeePolicy");

            migrationBuilder.DropColumn(
                name: "MinimumWithdrawal",
                schema: "merchant",
                table: "DefaultFeePolicy");
        }
    }
}
