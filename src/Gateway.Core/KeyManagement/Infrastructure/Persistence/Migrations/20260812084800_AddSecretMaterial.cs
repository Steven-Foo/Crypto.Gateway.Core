using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretMaterial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecretMaterial",
                schema: "keymgmt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Xpub = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    KmsKeyId = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretMaterial", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecretMaterial_Reference",
                schema: "keymgmt",
                table: "SecretMaterial",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecretMaterial",
                schema: "keymgmt");
        }
    }
}
