using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalScreening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScreeningDecision",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScreeningId",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreeningScore",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScreeningDecision",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "ScreeningId",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "ScreeningScore",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
