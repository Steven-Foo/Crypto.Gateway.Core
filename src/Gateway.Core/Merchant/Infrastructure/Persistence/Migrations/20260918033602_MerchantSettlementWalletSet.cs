using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Turns the single settlement wallet per (merchant, chain) into a set: several may be whitelisted, and
    /// exactly one is Active — the address that chain's cash-outs are actually paid to.
    ///
    /// <para>The <c>Status</c> default is load-bearing. Every existing row IS that merchant's cash-out
    /// destination, so it has to come out of this migration as <c>'Active'</c>; EF's generated default was an
    /// empty string, which would have matched no filtered index and left every merchant with no resolvable
    /// destination — every cash-out failing at request time with "settlement wallet not registered".</para>
    /// </summary>
    public partial class MerchantSettlementWalletSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MerchantSettlementWallet_MerchantId_Chain",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.AddColumn<string>(
                name: "Label",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateIndex(
                name: "UX_MerchantSettlementWallet_MerchantId_Chain_Active",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                columns: new[] { "MerchantId", "Chain" },
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "UX_MerchantSettlementWallet_MerchantId_Chain_Address",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                columns: new[] { "MerchantId", "Chain", "Address" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MerchantSettlementWallet_MerchantId_Chain_Active",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropIndex(
                name: "UX_MerchantSettlementWallet_MerchantId_Chain_Address",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "Label",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "merchant",
                table: "MerchantSettlementWallet");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantSettlementWallet_MerchantId_Chain",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                columns: new[] { "MerchantId", "Chain" },
                unique: true);
        }
    }
}
