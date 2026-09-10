using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditMerchantTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "audit",
                table: "AuditEntry",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_MerchantId_CreatedAt",
                schema: "audit",
                table: "AuditEntry",
                columns: new[] { "MerchantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntry_MerchantId_CreatedAt",
                schema: "audit",
                table: "AuditEntry");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "audit",
                table: "AuditEntry");
        }
    }
}
