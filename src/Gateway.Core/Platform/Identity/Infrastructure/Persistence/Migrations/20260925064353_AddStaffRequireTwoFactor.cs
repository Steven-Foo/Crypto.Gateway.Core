using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffRequireTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled true, not false: every existing account was, in effect, force-required before this
            // switch existed (there was no way to opt out), so the migration must not silently switch 2FA off
            // for every account already on the platform. New rows always pass an explicit value from the
            // application (StaffUser.Create / StaffSession.Issue) — this default only protects rows already
            // in the table when the column is added.
            migrationBuilder.AddColumn<bool>(
                name: "RequireTwoFactor",
                schema: "identity",
                table: "StaffUser",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Same reasoning as StaffUser above, for sessions already live at deploy time — an existing
            // session must keep demanding what it always demanded until it expires or the user logs in again.
            migrationBuilder.AddColumn<bool>(
                name: "RequireTwoFactor",
                schema: "identity",
                table: "StaffSession",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireTwoFactor",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.DropColumn(
                name: "RequireTwoFactor",
                schema: "identity",
                table: "StaffSession");
        }
    }
}
