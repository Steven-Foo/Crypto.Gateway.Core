using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalEnergyUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<BigInteger>(
                name: "EnergyUsed",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "decimal(38,0)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnergyUsed",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
