using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalSourceWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceWalletId",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Withdrawal_InFlight_SourceWallet",
                schema: "withdrawal",
                table: "Withdrawal",
                column: "SourceWalletId",
                unique: true,
                filter: "[Status] IN ('Signing', 'Broadcast')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Withdrawal_InFlight_SourceWallet",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "SourceWalletId",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
