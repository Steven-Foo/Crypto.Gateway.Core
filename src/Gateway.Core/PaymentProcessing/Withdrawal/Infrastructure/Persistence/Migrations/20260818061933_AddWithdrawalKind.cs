using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Withdrawal_MerchantTxn",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValueSql: "'User'");

            migrationBuilder.CreateIndex(
                name: "UX_Withdrawal_MerchantTxn",
                schema: "withdrawal",
                table: "Withdrawal",
                columns: new[] { "MerchantId", "Kind", "MerchantTransactionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Withdrawal_MerchantTxn",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.CreateIndex(
                name: "UX_Withdrawal_MerchantTxn",
                schema: "withdrawal",
                table: "Withdrawal",
                columns: new[] { "MerchantId", "MerchantTransactionId" },
                unique: true);
        }
    }
}
