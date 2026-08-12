using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalFundingHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleasedBy",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "ReleasedBy",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(24)",
                oldMaxLength: 24);
        }
    }
}
