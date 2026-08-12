using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnergyOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnergyOperation",
                schema: "energy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StakingWalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    TargetAddress = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    AmountSun = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SigningRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SignedTransaction = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    TransactionHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Confirmations = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergyOperation", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnergyOp_Status",
                schema: "energy",
                table: "EnergyOperation",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EnergyOperation_Seq",
                schema: "energy",
                table: "EnergyOperation",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_EnergyOp_InFlight_Delegate",
                schema: "energy",
                table: "EnergyOperation",
                columns: new[] { "Chain", "TargetAddress" },
                unique: true,
                filter: "[Kind] = 'Delegate' AND [Status] IN ('Pending', 'Signing', 'Broadcast')");

            migrationBuilder.CreateIndex(
                name: "UX_EnergyOp_InFlight_Stake",
                schema: "energy",
                table: "EnergyOperation",
                column: "StakingWalletId",
                unique: true,
                filter: "[Kind] = 'Stake' AND [Status] IN ('Pending', 'Signing', 'Broadcast')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnergyOperation",
                schema: "energy");
        }
    }
}
