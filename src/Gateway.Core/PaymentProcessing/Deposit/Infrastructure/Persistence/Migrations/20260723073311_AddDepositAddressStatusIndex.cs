using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepositAddressStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Deposit_Chain_Address_Status",
                schema: "deposit",
                table: "Deposit",
                columns: new[] { "Chain", "Address", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deposit_Chain_Address_Status",
                schema: "deposit",
                table: "Deposit");
        }
    }
}
