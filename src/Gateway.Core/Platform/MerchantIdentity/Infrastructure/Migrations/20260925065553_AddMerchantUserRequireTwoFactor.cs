using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantUserRequireTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled true, not false: every existing account/session was, in effect, force-required
            // before this switch existed, so the migration must not silently switch 2FA off for every portal
            // account already provisioned. New rows always pass an explicit value from the application
            // (MerchantUser.Create / MerchantUserSession.Issue).
            migrationBuilder.AddColumn<bool>(
                name: "RequireTwoFactor",
                schema: "merchantidentity",
                table: "MerchantUserSession",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireTwoFactor",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireTwoFactor",
                schema: "merchantidentity",
                table: "MerchantUserSession");

            migrationBuilder.DropColumn(
                name: "RequireTwoFactor",
                schema: "merchantidentity",
                table: "MerchantUser");
        }
    }
}
