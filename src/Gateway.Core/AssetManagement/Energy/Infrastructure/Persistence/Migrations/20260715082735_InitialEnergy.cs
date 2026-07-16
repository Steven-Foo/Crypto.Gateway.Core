using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEnergy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "energy");

            migrationBuilder.CreateTable(
                name: "EnergyPolicy",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    WalletType = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    MinimumEnergy = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    TargetEnergy = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    StakeThreshold = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    RentalThreshold = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    EnableAutoStake = table.Column<bool>(type: "bit", nullable: false),
                    EnableAutoRent = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergyPolicy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "energy",
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

            migrationBuilder.CreateIndex(
                name: "IX_EnergyPolicy_Chain_WalletType",
                schema: "energy",
                table: "EnergyPolicy",
                columns: new[] { "Chain", "WalletType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "energy",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "energy",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnergyPolicy",
                schema: "energy");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "energy");
        }
    }
}
