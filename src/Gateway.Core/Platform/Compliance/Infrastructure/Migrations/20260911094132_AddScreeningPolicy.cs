using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScreeningPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScreeningPolicyVersion",
                schema: "compliance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlockScore = table.Column<int>(type: "int", nullable: false),
                    ReviewScore = table.Column<int>(type: "int", nullable: false),
                    CacheDays = table.Column<int>(type: "int", nullable: false),
                    IndirectReviewMaxHops = table.Column<int>(type: "int", nullable: false),
                    IndirectReviewMinPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ExtraAlwaysBlockIndicatorsCsv = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreeningPolicyVersion", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningPolicyVersion_Seq",
                schema: "compliance",
                table: "ScreeningPolicyVersion",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreeningPolicyVersion_UpdatedAt",
                schema: "compliance",
                table: "ScreeningPolicyVersion",
                column: "UpdatedAt",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreeningPolicyVersion",
                schema: "compliance");
        }
    }
}
