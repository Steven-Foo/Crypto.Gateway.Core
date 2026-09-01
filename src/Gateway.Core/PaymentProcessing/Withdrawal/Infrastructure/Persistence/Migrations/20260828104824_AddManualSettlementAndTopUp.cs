using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManualSettlementAndTopUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuditedAt",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuditedBy",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementSourceAddress",
                schema: "withdrawal",
                table: "Withdrawal",
                type: "varchar(128)",
                unicode: false,
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HotWalletTopUp",
                schema: "withdrawal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetWalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Amount = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    TransactionHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    SourceAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotWalletTopUp", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Withdrawal_SettlementTxHash",
                schema: "withdrawal",
                table: "Withdrawal",
                column: "TransactionHash",
                unique: true,
                filter: "[SettlementSourceAddress] IS NOT NULL AND [TransactionHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HotWalletTopUp_Chain_RecordedAt",
                schema: "withdrawal",
                table: "HotWalletTopUp",
                columns: new[] { "Chain", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HotWalletTopUp_Seq",
                schema: "withdrawal",
                table: "HotWalletTopUp",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_HotWalletTopUp_TxHash",
                schema: "withdrawal",
                table: "HotWalletTopUp",
                column: "TransactionHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HotWalletTopUp",
                schema: "withdrawal");

            migrationBuilder.DropIndex(
                name: "UX_Withdrawal_SettlementTxHash",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "AuditedAt",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "AuditedBy",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                schema: "withdrawal",
                table: "Withdrawal");

            migrationBuilder.DropColumn(
                name: "SettlementSourceAddress",
                schema: "withdrawal",
                table: "Withdrawal");
        }
    }
}
