using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnergyTopUpIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_EnergyOp_InFlight_TopUp",
                schema: "energy",
                table: "EnergyOperation",
                columns: new[] { "Chain", "TargetAddress" },
                unique: true,
                filter: "[Kind] = 'TopUp' AND [Status] IN ('Pending', 'Signing', 'Broadcast')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_EnergyOp_InFlight_TopUp",
                schema: "energy",
                table: "EnergyOperation");
        }
    }
}
