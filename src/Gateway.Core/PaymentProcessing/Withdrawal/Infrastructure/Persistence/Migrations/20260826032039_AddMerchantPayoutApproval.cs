using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantPayoutApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MerchantApprovedAt",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantApprovedBy",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MerchantApprovedAt",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "MerchantApprovedBy",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
