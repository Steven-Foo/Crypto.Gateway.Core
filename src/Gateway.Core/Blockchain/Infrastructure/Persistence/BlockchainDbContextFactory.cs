using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Persistence;

/// <summary>
/// Design-time only: lets <c>dotnet ef migrations</c> build the context without booting a host.
/// Reads <c>CPE_DB_CONNECTION</c>, falling back to LocalDB for local development. Never used at runtime.
/// </summary>
public sealed class BlockchainDbContextFactory : IDesignTimeDbContextFactory<BlockchainDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public BlockchainDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<BlockchainDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", BlockchainDbContext.SchemaName))
            .UseBigIntegerMoney()
            .Options;

        return new BlockchainDbContext(options);
    }
}
