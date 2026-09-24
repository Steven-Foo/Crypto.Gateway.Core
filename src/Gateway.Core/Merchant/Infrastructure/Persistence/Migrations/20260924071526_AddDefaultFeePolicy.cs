using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultFeePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DefaultFeePolicy",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepositFeeFixed = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    DepositFeeBps = table.Column<int>(type: "int", nullable: false),
                    MinimumDepositFee = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    WithdrawalFee = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    WithdrawalFeeBps = table.Column<int>(type: "int", nullable: false),
                    MinimumWithdrawalFee = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefaultFeePolicy", x => x.Id);
                    table.CheckConstraint("CK_DefaultFeePolicy_FeeBps", "[DepositFeeBps] >= 0 AND [DepositFeeBps] <= 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000");
                    table.CheckConstraint("CK_DefaultFeePolicy_NonNegative", "[DepositFeeFixed] >= 0 AND [WithdrawalFee] >= 0 AND [MinimumDepositFee] >= 0 AND [MinimumWithdrawalFee] >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DefaultFeePolicy_AssetId",
                schema: "merchant",
                table: "DefaultFeePolicy",
                column: "AssetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DefaultFeePolicy",
                schema: "merchant");
        }
    }
}
