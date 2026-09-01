using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantRolesAndAccountLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MerchantRole",
                schema: "merchantidentity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PermissionCodesCsv = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantRole", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUser_RoleId",
                schema: "merchantidentity",
                table: "MerchantUser",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "UX_MerchantRole_Merchant_Name",
                schema: "merchantidentity",
                table: "MerchantRole",
                columns: new[] { "MerchantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MerchantUser_MerchantRole_RoleId",
                schema: "merchantidentity",
                table: "MerchantUser",
                column: "RoleId",
                principalSchema: "merchantidentity",
                principalTable: "MerchantRole",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MerchantUser_MerchantRole_RoleId",
                schema: "merchantidentity",
                table: "MerchantUser");

            migrationBuilder.DropTable(
                name: "MerchantRole",
                schema: "merchantidentity");

            migrationBuilder.DropIndex(
                name: "IX_MerchantUser_RoleId",
                schema: "merchantidentity",
                table: "MerchantUser");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "merchantidentity",
                table: "MerchantUser");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "merchantidentity",
                table: "MerchantUser");
        }
    }
}
