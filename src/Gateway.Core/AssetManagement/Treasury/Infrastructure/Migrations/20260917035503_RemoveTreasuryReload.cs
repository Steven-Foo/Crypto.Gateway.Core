using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTreasuryReload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TreasuryReload",
                schema: "treasury");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TreasuryReload",
                schema: "treasury",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Confirmations = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SignedTransaction = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SourceAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    TargetAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    TargetWalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    UnsignedPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryReload", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryReload_Seq",
                schema: "treasury",
                table: "TreasuryReload",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryReload_Status",
                schema: "treasury",
                table: "TreasuryReload",
                column: "Status");
        }
    }
}
