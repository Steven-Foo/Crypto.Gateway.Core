using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHdWalletIsImported : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_HdWallet_PublicKeyReference_MatchesScheme",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.AddColumn<bool>(
                name: "IsImported",
                schema: "keymgmt",
                table: "HdWallet",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_HdWallet_PublicKeyReference_MatchesScheme",
                schema: "keymgmt",
                table: "HdWallet",
                sql: "([IsImported] = 1 AND [PublicKeyReference] IS NULL) OR ([IsImported] = 0 AND [Scheme] = 'Bip32Secp256k1' AND [PublicKeyReference] IS NOT NULL) OR ([IsImported] = 0 AND [Scheme] = 'Slip10Ed25519' AND [PublicKeyReference] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_HdWallet_PublicKeyReference_MatchesScheme",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.DropColumn(
                name: "IsImported",
                schema: "keymgmt",
                table: "HdWallet");

            migrationBuilder.AddCheckConstraint(
                name: "CK_HdWallet_PublicKeyReference_MatchesScheme",
                schema: "keymgmt",
                table: "HdWallet",
                sql: "([Scheme] = 'Bip32Secp256k1' AND [PublicKeyReference] IS NOT NULL) OR ([Scheme] = 'Slip10Ed25519' AND [PublicKeyReference] IS NULL)");
        }
    }
}
