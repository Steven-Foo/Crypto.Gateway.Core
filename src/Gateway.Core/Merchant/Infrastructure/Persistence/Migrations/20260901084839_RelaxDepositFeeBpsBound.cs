using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelaxDepositFeeBpsBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[DepositFeeBps] >= 0 AND [DepositFeeBps] <= 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000 AND [TopUpFeeBps] >= 0 AND [TopUpFeeBps] <= 10000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_FeeBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[DepositFeeBps] >= 0 AND [DepositFeeBps] < 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000 AND [TopUpFeeBps] >= 0 AND [TopUpFeeBps] <= 10000");
        }
    }
}
