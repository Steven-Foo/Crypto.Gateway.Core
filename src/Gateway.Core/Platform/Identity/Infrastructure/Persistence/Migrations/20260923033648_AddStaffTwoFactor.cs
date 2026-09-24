using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TwoFactorMethod",
                schema: "identity",
                table: "StaffSession",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffRecoveryCode",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffRecoveryCode", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "StaffTwoFactor",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_StaffTwoFactor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TwoFactorPolicyVersion",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuardedActionsCsv = table.Column<string>(type: "varchar(4000)", unicode: false, maxLength: 4000, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwoFactorPolicyVersion", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffRecoveryCode_Seq",
                schema: "identity",
                table: "StaffRecoveryCode",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffRecoveryCode_StaffUserId_UsedAt",
                schema: "identity",
                table: "StaffRecoveryCode",
                columns: new[] { "StaffUserId", "UsedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffTwoFactor_StaffUserId",
                schema: "identity",
                table: "StaffTwoFactor",
                column: "StaffUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TwoFactorPolicyVersion_Seq",
                schema: "identity",
                table: "TwoFactorPolicyVersion",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_TwoFactorPolicyVersion_UpdatedAt",
                schema: "identity",
                table: "TwoFactorPolicyVersion",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffRecoveryCode",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "StaffTwoFactor",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "TwoFactorPolicyVersion",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "TwoFactorMethod",
                schema: "identity",
                table: "StaffSession");
        }
    }
}
