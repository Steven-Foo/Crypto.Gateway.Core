using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepositFeeMinimumSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FeeBps",
                schema: "deposit",
                table: "Deposit",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<BigInteger>(
                name: "FeeFixed",
                schema: "deposit",
                table: "Deposit",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<BigInteger>(
                name: "FeeMinimum",
                schema: "deposit",
                table: "Deposit",
                type: "decimal(38,0)",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<bool>(
                name: "MinimumFeeApplied",
                schema: "deposit",
                table: "Deposit",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeeBps",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.DropColumn(
                name: "FeeFixed",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.DropColumn(
                name: "FeeMinimum",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.DropColumn(
                name: "MinimumFeeApplied",
                schema: "deposit",
                table: "Deposit");
        }
    }
}
