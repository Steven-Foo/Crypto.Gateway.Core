using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantUserPrimaryFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                schema: "merchantidentity",
                table: "MerchantUser",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Backfill: every merchant that already has accounts gets its EARLIEST-created one marked primary,
            // matching the rule new accounts follow from here on (MerchantAccountService.CreateAsync — "the
            // merchant's first account is its permanent super-admin"). Ordered by CreatedAt only, deliberately
            // NOT by Id as a tiebreak: SQL Server orders a uniqueidentifier by its last six bytes, unrelated to
            // insertion order even for a Guid.CreateVersion7() PK (this table carries no Seq column). A
            // millisecond-exact tie between two account creations is not a real-world scenario worth a
            // meaningful tiebreak. Runs before the unique index below is created, so by construction there is
            // never more than one match per merchant for it to reject.
            //
            // Wrapped in EXEC(): this references [IsPrimary], added by the ALTER TABLE directly above in the
            // SAME batch. SQL Server compiles a whole batch before executing any of it, and — unlike a wholly
            // new table — an existing table's column list is validated at compile time, so an unwrapped
            // reference here fails with "Invalid column name 'IsPrimary'" even though the ALTER TABLE runs
            // first at execution time. EXEC() defers parsing to its own runtime step, after the column exists.
            migrationBuilder.Sql("""
                EXEC(N'
                ;WITH Ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [MerchantId] ORDER BY [CreatedAt] ASC) AS [Rn]
                    FROM [merchantidentity].[MerchantUser]
                )
                UPDATE [u]
                SET [u].[IsPrimary] = 1
                FROM [merchantidentity].[MerchantUser] AS [u]
                INNER JOIN [Ranked] ON [Ranked].[Id] = [u].[Id]
                WHERE [Ranked].[Rn] = 1;
                ');
                """);

            migrationBuilder.CreateIndex(
                name: "UX_MerchantUser_MerchantId_Primary",
                schema: "merchantidentity",
                table: "MerchantUser",
                column: "MerchantId",
                unique: true,
                filter: "[IsPrimary] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MerchantUser_MerchantId_Primary",
                schema: "merchantidentity",
                table: "MerchantUser");

            migrationBuilder.DropColumn(
                name: "IsPrimary",
                schema: "merchantidentity",
                table: "MerchantUser");
        }
    }
}
