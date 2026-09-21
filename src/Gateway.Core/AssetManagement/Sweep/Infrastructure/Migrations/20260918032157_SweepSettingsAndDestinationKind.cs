using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Migrations
{
    /// <summary>
    /// Records WHY each sweep went where it went (the collection wallet's kind, and the screening evidence
    /// behind that choice), and adds the per-chain sweep dials + schedule state staff can now change from
    /// the back office.
    ///
    /// <para>Existing sweeps are backfilled <c>DestinationKind='Safe'</c>: every sweep made before taint
    /// segregation existed went to the one and only cold destination, which the Treasury migration also
    /// turns into the Safe one. EF's generated default was an empty string, which would have left historical
    /// sweeps reading as neither kind.</para>
    /// </summary>
    public partial class SweepSettingsAndDestinationKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationKind",
                schema: "sweep",
                table: "Sweep",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Safe");

            migrationBuilder.AddColumn<string>(
                name: "ScreeningDecision",
                schema: "sweep",
                table: "Sweep",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScreeningId",
                schema: "sweep",
                table: "Sweep",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SweepSettings",
                schema: "sweep",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    MinSweepAmount = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    Confirmations = table.Column<int>(type: "int", nullable: false),
                    ScanIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    IsStaffConfigured = table.Column<bool>(type: "bit", nullable: false),
                    ScanRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastScanStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastScanCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSweepsCreated = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SweepSettings_Chain",
                schema: "sweep",
                table: "SweepSettings",
                column: "Chain",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SweepSettings",
                schema: "sweep");

            migrationBuilder.DropColumn(
                name: "DestinationKind",
                schema: "sweep",
                table: "Sweep");

            migrationBuilder.DropColumn(
                name: "ScreeningDecision",
                schema: "sweep",
                table: "Sweep");

            migrationBuilder.DropColumn(
                name: "ScreeningId",
                schema: "sweep",
                table: "Sweep");
        }
    }
}
