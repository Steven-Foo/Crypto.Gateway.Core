using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantAllowedIps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedIpsCsv",
                schema: "merchant",
                table: "MerchantConfiguration",
                type: "varchar(2048)",
                unicode: false,
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedIpsCsv",
                schema: "merchant",
                table: "MerchantConfiguration");
        }
    }
}
