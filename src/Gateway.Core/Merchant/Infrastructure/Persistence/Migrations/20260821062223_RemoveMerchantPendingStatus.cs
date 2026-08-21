using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMerchantPendingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Status stays nvarchar(16) — no column change. MerchantStatus.Pending is retired (nothing ever
            // left a merchant sitting in it in practice; every creation path activated immediately), so any
            // stray 'Pending' row would otherwise fail to parse back into the enum. Same precedent as the
            // Suspended -> Frozen rewrite in AddMerchantSettlementDelay.
            migrationBuilder.Sql("UPDATE [merchant].[Merchant] SET [Status] = 'Active' WHERE [Status] = 'Pending';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately no-op: which rows were originally Pending vs genuinely Active is no longer
            // recoverable once this has run, so there is nothing safe to revert.
        }
    }
}
