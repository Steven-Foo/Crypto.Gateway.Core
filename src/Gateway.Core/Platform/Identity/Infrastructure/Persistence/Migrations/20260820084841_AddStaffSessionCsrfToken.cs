using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffSessionCsrfToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CsrfToken",
                schema: "identity",
                table: "StaffSession",
                type: "varchar(128)",
                unicode: false,
                maxLength: 128,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CsrfToken",
                schema: "identity",
                table: "StaffSession");
        }
    }
}
