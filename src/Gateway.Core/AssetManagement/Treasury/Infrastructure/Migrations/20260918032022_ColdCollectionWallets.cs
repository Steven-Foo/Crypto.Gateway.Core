using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Migrations
{
    /// <summary>
    /// Turns the single cold treasury address per chain into a set of cold COLLECTION wallets: several per
    /// chain, one active destination per (chain, kind), with Safe and Danger as the two kinds so flagged
    /// sweeps can be segregated from clean ones.
    ///
    /// <para><b>The default values are load-bearing, not boilerplate.</b> Any address already registered is
    /// the chain's live sweep destination, so it has to come out of this migration as <c>Kind='Safe'</c> and
    /// <c>Status='Active'</c>. EF's generated defaults were empty strings, which would have left the running
    /// platform with no active destination — sweeps would have stopped silently on the next pass.</para>
    /// </summary>
    public partial class ColdCollectionWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TreasuryColdWallet_Chain",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Safe");

            migrationBuilder.AddColumn<string>(
                name: "Label",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScreenedAt",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreeningDecision",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScreeningId",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreeningScore",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "treasury",
                table: "TreasuryColdWallet",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryColdWallet_Chain_Address",
                schema: "treasury",
                table: "TreasuryColdWallet",
                columns: new[] { "Chain", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryColdWallet_Chain_Kind_Active",
                schema: "treasury",
                table: "TreasuryColdWallet",
                columns: new[] { "Chain", "Kind" },
                unique: true,
                filter: "[Status] = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TreasuryColdWallet_Chain_Address",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropIndex(
                name: "UX_TreasuryColdWallet_Chain_Kind_Active",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "Label",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "ScreenedAt",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "ScreeningDecision",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "ScreeningId",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "ScreeningScore",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "treasury",
                table: "TreasuryColdWallet");

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryColdWallet_Chain",
                schema: "treasury",
                table: "TreasuryColdWallet",
                column: "Chain",
                unique: true);
        }
    }
}
