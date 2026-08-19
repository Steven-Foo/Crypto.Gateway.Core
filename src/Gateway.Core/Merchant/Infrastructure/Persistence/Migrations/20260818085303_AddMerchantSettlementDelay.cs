using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantSettlementDelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SettlementDelayDays",
                schema: "merchant",
                table: "Merchant",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // The MerchantStatus enum value 'Suspended' was renamed to 'Frozen' (stored as its string). No
            // production row is ever 'Suspended' today (nothing set it), but rename any that exist so a merchant
            // frozen before this migration keeps a valid, blocked status. Safe no-op on zero rows.
            migrationBuilder.Sql("UPDATE [merchant].[Merchant] SET [Status] = 'Frozen' WHERE [Status] = 'Suspended';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [merchant].[Merchant] SET [Status] = 'Suspended' WHERE [Status] = 'Frozen';");

            migrationBuilder.DropColumn(
                name: "SettlementDelayDays",
                schema: "merchant",
                table: "Merchant");
        }
    }
}
