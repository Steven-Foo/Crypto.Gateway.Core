using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepositsReceivedCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepositsReceivedCount",
                schema: "wallet",
                table: "Wallet",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepositsReceivedCount",
                schema: "wallet",
                table: "Wallet");
        }
    }
}
