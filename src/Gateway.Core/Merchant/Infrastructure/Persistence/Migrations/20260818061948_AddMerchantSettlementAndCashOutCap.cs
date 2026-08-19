using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantSettlementAndCashOutCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<BigInteger>(
                name: "MerchantWithdrawalFlatCap",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "decimal(38,0)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MerchantWithdrawalPercentBps",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MerchantSettlementWallet",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantSettlementWallet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantSettlementWallet_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merchant",
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MerchantAssetPolicy_MerchantWithdrawalCap",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                sql: "[MerchantWithdrawalPercentBps] >= 0 AND [MerchantWithdrawalPercentBps] <= 10000 AND ([MerchantWithdrawalFlatCap] IS NULL OR [MerchantWithdrawalFlatCap] >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantSettlementWallet_MerchantId_Chain",
                schema: "merchant",
                table: "MerchantSettlementWallet",
                columns: new[] { "MerchantId", "Chain" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MerchantSettlementWallet",
                schema: "merchant");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MerchantAssetPolicy_MerchantWithdrawalCap",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MerchantWithdrawalFlatCap",
                schema: "merchant",
                table: "MerchantAssetPolicy");

            migrationBuilder.DropColumn(
                name: "MerchantWithdrawalPercentBps",
                schema: "merchant",
                table: "MerchantAssetPolicy");
        }
    }
}
