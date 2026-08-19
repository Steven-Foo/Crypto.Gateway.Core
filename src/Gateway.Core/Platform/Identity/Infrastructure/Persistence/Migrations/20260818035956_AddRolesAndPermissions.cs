using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                schema: "identity",
                table: "StaffSession");

            // NOT a rename: the old StaffUser.Role column held "Admin"/"Viewer" — semantically unrelated to
            // the new Status column ("Active"/"Disabled"). A straight rename would leave that old text
            // sitting in a column that means something different, so this drops it and adds Status fresh.
            migrationBuilder.DropColumn(
                name: "Role",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "identity",
                table: "StaffUser",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "identity",
                table: "StaffUser",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "PermissionCodesCsv",
                schema: "identity",
                table: "StaffSession",
                type: "varchar(2048)",
                unicode: false,
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "identity",
                table: "StaffSession",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "RoleName",
                schema: "identity",
                table: "StaffSession",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PermissionCodesCsv = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffUser_RoleId",
                schema: "identity",
                table: "StaffUser",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Role_Name",
                schema: "identity",
                table: "Role",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffUser_Role_RoleId",
                schema: "identity",
                table: "StaffUser",
                column: "RoleId",
                principalSchema: "identity",
                principalTable: "Role",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StaffUser_Role_RoleId",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_StaffUser_RoleId",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.DropColumn(
                name: "PermissionCodesCsv",
                schema: "identity",
                table: "StaffSession");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "identity",
                table: "StaffSession");

            migrationBuilder.DropColumn(
                name: "RoleName",
                schema: "identity",
                table: "StaffSession");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "identity",
                table: "StaffUser");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "identity",
                table: "StaffUser",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "identity",
                table: "StaffSession",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }
    }
}
