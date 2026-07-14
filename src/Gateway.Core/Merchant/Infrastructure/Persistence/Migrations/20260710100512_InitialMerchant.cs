using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMerchant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "merchant");

            migrationBuilder.CreateTable(
                name: "Merchant",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CallbackUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchant", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantWebhook",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastResponse = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantWebhook", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "merchant",
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
                name: "MerchantApiCredential",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SecretHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    HashVersion = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantApiCredential", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantApiCredential_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merchant",
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantAssetPolicy",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SweepThreshold = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    MinimumWithdrawal = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    MaximumWithdrawal = table.Column<BigInteger>(type: "decimal(38,0)", nullable: true),
                    WithdrawalFee = table.Column<BigInteger>(type: "decimal(38,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAssetPolicy", x => x.Id);
                    table.CheckConstraint("CK_MerchantAssetPolicy_NonNegative", "[SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");
                    table.CheckConstraint("CK_MerchantAssetPolicy_WithdrawalRange", "[MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]");
                    table.ForeignKey(
                        name: "FK_MerchantAssetPolicy_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merchant",
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantConfiguration",
                schema: "merchant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutoSweepEnabled = table.Column<bool>(type: "bit", nullable: false),
                    WebhookRetryCount = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantConfiguration", x => x.Id);
                    table.CheckConstraint("CK_MerchantConfiguration_WebhookRetryCount", "[WebhookRetryCount] >= 0 AND [WebhookRetryCount] <= 20");
                    table.ForeignKey(
                        name: "FK_MerchantConfiguration_Merchant_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merchant",
                        principalTable: "Merchant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Merchant_MerchantCode",
                schema: "merchant",
                table: "Merchant",
                column: "MerchantCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantApiCredential_ApiKey",
                schema: "merchant",
                table: "MerchantApiCredential",
                column: "ApiKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantApiCredential_MerchantId_Status",
                schema: "merchant",
                table: "MerchantApiCredential",
                columns: new[] { "MerchantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAssetPolicy_MerchantId_AssetId",
                schema: "merchant",
                table: "MerchantAssetPolicy",
                columns: new[] { "MerchantId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantConfiguration_MerchantId",
                schema: "merchant",
                table: "MerchantConfiguration",
                column: "MerchantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantWebhook_MerchantId",
                schema: "merchant",
                table: "MerchantWebhook",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantWebhook_Seq",
                schema: "merchant",
                table: "MerchantWebhook",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantWebhook_Status_NextRetryAt",
                schema: "merchant",
                table: "MerchantWebhook",
                columns: new[] { "Status", "NextRetryAt" },
                filter: "[NextRetryAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedOnUtc",
                schema: "merchant",
                table: "OutboxMessage",
                column: "ProcessedOnUtc",
                filter: "[ProcessedOnUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Seq",
                schema: "merchant",
                table: "OutboxMessage",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MerchantApiCredential",
                schema: "merchant");

            migrationBuilder.DropTable(
                name: "MerchantAssetPolicy",
                schema: "merchant");

            migrationBuilder.DropTable(
                name: "MerchantConfiguration",
                schema: "merchant");

            migrationBuilder.DropTable(
                name: "MerchantWebhook",
                schema: "merchant");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "merchant");

            migrationBuilder.DropTable(
                name: "Merchant",
                schema: "merchant");
        }
    }
}
