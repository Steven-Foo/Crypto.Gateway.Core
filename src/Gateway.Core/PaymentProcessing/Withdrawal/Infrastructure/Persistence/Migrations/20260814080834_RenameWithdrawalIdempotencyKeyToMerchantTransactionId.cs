using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameWithdrawalIdempotencyKeyToMerchantTransactionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IdempotencyKey",
                schema: "withdrawal",
                table: "Withdrawal",
                newName: "MerchantTransactionId");

            migrationBuilder.RenameIndex(
                name: "UX_Withdrawal_Idempotency",
                schema: "withdrawal",
                table: "Withdrawal",
                newName: "UX_Withdrawal_MerchantTxn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MerchantTransactionId",
                schema: "withdrawal",
                table: "Withdrawal",
                newName: "IdempotencyKey");

            migrationBuilder.RenameIndex(
                name: "UX_Withdrawal_MerchantTxn",
                schema: "withdrawal",
                table: "Withdrawal",
                newName: "UX_Withdrawal_Idempotency");
        }
    }
}
