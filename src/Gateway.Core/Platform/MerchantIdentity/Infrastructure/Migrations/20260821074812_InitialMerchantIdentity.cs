using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMerchantIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "merchantidentity");

            migrationBuilder.CreateTable(
                name: "MerchantUser",
                schema: "merchantidentity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUser", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantUserSession",
                schema: "merchantidentity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    CsrfToken = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    PermissionCodesCsv = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserSession", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "merchantidentity",
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
                name: "IX_MerchantUser_MerchantId",
                schema: "merchantidentity",
                table: "MerchantUser",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUser_Username",
                schema: "merchantidentity",
                table: "MerchantUser",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserSession_MerchantUserId",
                schema: "merchantidentity",
                table: "MerchantUserSession",
                column: "MerchantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserSession_Seq",
                schema: "merchantidentity",
                table: "MerchantUserSession",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserSession_TokenHash",
                schema: "merchantidentity",
                table: "MerchantUserSession",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "merchantidentity",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "merchantidentity",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MerchantUser",
                schema: "merchantidentity");

            migrationBuilder.DropTable(
                name: "MerchantUserSession",
                schema: "merchantidentity");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "merchantidentity");
        }
    }
}
