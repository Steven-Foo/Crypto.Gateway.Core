using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTreasury : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "treasury");

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "treasury",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredOnUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedOnUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryColdWallet",
                schema: "treasury",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryColdWallet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryReload",
                schema: "treasury",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    TargetWalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Amount = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    UnsignedPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SignedTransaction = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    TransactionHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    Confirmations = table.Column<int>(type: "int", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryReload", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "treasury",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "treasury",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryColdWallet_Chain",
                schema: "treasury",
                table: "TreasuryColdWallet",
                column: "Chain",
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "treasury");

            migrationBuilder.DropTable(
                name: "TreasuryColdWallet",
                schema: "treasury");

            migrationBuilder.DropTable(
                name: "TreasuryReload",
                schema: "treasury");
        }
    }
}
