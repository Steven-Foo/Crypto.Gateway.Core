using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DepositFinalizedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deposit_Chain_Status",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalizedAt",
                schema: "deposit",
                table: "Deposit",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deposit_Chain_Status",
                schema: "deposit",
                table: "Deposit",
                columns: new[] { "Chain", "Status" },
                filter: "[FinalizedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deposit_Chain_Status",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.DropColumn(
                name: "FinalizedAt",
                schema: "deposit",
                table: "Deposit");

            migrationBuilder.CreateIndex(
                name: "IX_Deposit_Chain_Status",
                schema: "deposit",
                table: "Deposit",
                columns: new[] { "Chain", "Status" });
        }
    }
}
