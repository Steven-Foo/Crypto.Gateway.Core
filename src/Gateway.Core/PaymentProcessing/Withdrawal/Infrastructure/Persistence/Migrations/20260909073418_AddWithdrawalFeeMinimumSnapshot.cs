using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalFeeMinimumSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FeeBps",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<BigInteger>(
                name: "FeeFixed",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<BigInteger>(
                name: "FeeMinimum",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<bool>(
                name: "MinimumFeeApplied",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeeBps",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "FeeFixed",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "FeeMinimum",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "MinimumFeeApplied",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
