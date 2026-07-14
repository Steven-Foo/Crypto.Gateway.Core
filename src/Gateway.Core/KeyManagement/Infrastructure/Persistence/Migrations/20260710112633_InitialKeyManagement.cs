using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialKeyManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "keymgmt");

            migrationBuilder.CreateTable(
                name: "HdWallet",
                schema: "keymgmt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scheme = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SecretProvider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SecretReference = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    PublicKeyReference = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    DerivationPath = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    NextDerivationIndex = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HdWallet", x => x.Id);
                    table.CheckConstraint("CK_HdWallet_DerivationIndex_Range", "[NextDerivationIndex] >= 0 AND [NextDerivationIndex] <= 2147483648");
                    table.CheckConstraint("CK_HdWallet_PublicKeyReference_MatchesScheme", "([Scheme] = 'Bip32Secp256k1' AND [PublicKeyReference] IS NOT NULL) OR ([Scheme] = 'Slip10Ed25519' AND [PublicKeyReference] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "keymgmt",
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
                name: "DerivedKey",
                schema: "keymgmt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HdWalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DerivationIndex = table.Column<long>(type: "bigint", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    DerivationPath = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedKey", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_DerivedKey_DerivationIndex_Range", "[DerivationIndex] >= 0 AND [DerivationIndex] <= 2147483647");
                    table.ForeignKey(
                        name: "FK_DerivedKey_HdWallet_HdWalletId",
                        column: x => x.HdWalletId,
                        principalSchema: "keymgmt",
                        principalTable: "HdWallet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DerivedKey_Chain_Address",
                schema: "keymgmt",
                table: "DerivedKey",
                columns: new[] { "Chain", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DerivedKey_HdWalletId_DerivationIndex",
                schema: "keymgmt",
                table: "DerivedKey",
                columns: new[] { "HdWalletId", "DerivationIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DerivedKey_Seq",
                schema: "keymgmt",
                table: "DerivedKey",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HdWallet_Chain_Purpose",
                schema: "keymgmt",
                table: "HdWallet",
                columns: new[] { "Chain", "Purpose" },
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "keymgmt",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "keymgmt",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DerivedKey",
                schema: "keymgmt");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "keymgmt");

            migrationBuilder.DropTable(
                name: "HdWallet",
                schema: "keymgmt");
        }
    }
}
