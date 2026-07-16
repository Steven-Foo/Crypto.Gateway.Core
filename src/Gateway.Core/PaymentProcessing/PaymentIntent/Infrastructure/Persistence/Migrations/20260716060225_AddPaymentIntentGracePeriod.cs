using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentIntentGracePeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentIntent_Status_Expiry",
                schema: "paymentintent",
                table: "PaymentIntent");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GraceExpiresAt",
                schema: "paymentintent",
                table: "PaymentIntent",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIntent_Status_GraceExpiry",
                schema: "paymentintent",
                table: "PaymentIntent",
                columns: new[] { "Status", "GraceExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentIntent_Status_GraceExpiry",
                schema: "paymentintent",
                table: "PaymentIntent");

            migrationBuilder.DropColumn(
                name: "GraceExpiresAt",
                schema: "paymentintent",
                table: "PaymentIntent");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIntent_Status_Expiry",
                schema: "paymentintent",
                table: "PaymentIntent",
                columns: new[] { "Status", "ExpiresAt" });
        }
    }
}
