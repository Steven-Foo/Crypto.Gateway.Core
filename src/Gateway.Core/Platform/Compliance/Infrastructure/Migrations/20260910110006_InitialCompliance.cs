using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            migrationBuilder.CreateTable(
                name: "AddressScreening",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Score = table.Column<int>(type: "int", nullable: true),
                    RiskLevel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ReasonsCsv = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    AddressLabel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReportUrl = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    PolicyDescription = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ScreenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FreshUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressScreening", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "compliance",
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

            migrationBuilder.CreateIndex(
                name: "IX_AddressScreening_Chain_Address_ScreenedAt",
                schema: "compliance",
                table: "AddressScreening",
                columns: new[] { "Chain", "Address", "ScreenedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AddressScreening_Decision_ScreenedAt",
                schema: "compliance",
                table: "AddressScreening",
                columns: new[] { "Decision", "ScreenedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AddressScreening_Seq",
                schema: "compliance",
                table: "AddressScreening",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "compliance",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "compliance",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddressScreening",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "compliance");
        }
    }
}
