using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantUserTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TwoFactorMethod",
                schema: "merchantidentity",
                table: "MerchantUserSession",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MerchantUserRecoveryCode",
                schema: "merchantidentity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserRecoveryCode", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "MerchantUserTwoFactor",
                schema: "merchantidentity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretCiphertext = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EnrolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserTwoFactor", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserRecoveryCode_MerchantUserId_UsedAt",
                schema: "merchantidentity",
                table: "MerchantUserRecoveryCode",
                columns: new[] { "MerchantUserId", "UsedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserRecoveryCode_Seq",
                schema: "merchantidentity",
                table: "MerchantUserRecoveryCode",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserTwoFactor_MerchantId_MerchantUserId",
                schema: "merchantidentity",
                table: "MerchantUserTwoFactor",
                columns: new[] { "MerchantId", "MerchantUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserTwoFactor_MerchantUserId",
                schema: "merchantidentity",
                table: "MerchantUserTwoFactor",
                column: "MerchantUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MerchantUserRecoveryCode",
                schema: "merchantidentity");

            migrationBuilder.DropTable(
                name: "MerchantUserTwoFactor",
                schema: "merchantidentity");

            migrationBuilder.DropColumn(
                name: "TwoFactorMethod",
                schema: "merchantidentity",
                table: "MerchantUserSession");
        }
    }
}
