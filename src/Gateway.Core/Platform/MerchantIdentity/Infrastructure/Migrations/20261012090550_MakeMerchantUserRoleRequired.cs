using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeMerchantUserRoleRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every portal account must have a role (product decision 2026-10-12). EF scaffolded this with
            // defaultValue Guid.Empty, which would silently stamp an all-zero "role" onto any account that had none
            // — a fake value pointing at no role. Refuse instead: if any account still has no role, stop and let a
            // human assign one. (Staging was checked empty before this shipped.)
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [merchantidentity].[MerchantUser] WHERE [RoleId] IS NULL) BEGIN " +
                "IF @@TRANCOUNT > 0 ROLLBACK; " +
                "THROW 50001, 'MakeMerchantUserRoleRequired: some merchant portal accounts have no role. Assign every account a role, then run this again.', 1; END");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }
    }
}
