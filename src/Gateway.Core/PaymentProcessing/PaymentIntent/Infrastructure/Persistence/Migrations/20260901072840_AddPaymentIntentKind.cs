using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentIntentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "paymentintent",
                table: "PaymentIntent",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValueSql: "'Customer'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "paymentintent",
                table: "PaymentIntent");
        }
    }
}
