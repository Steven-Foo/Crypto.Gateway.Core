using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

/// <summary>Design-time only: lets <c>dotnet ef</c> build the context without booting a host.</summary>
public sealed class SweepDbContextFactory : IDesignTimeDbContextFactory<SweepDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public SweepDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<SweepDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SweepDbContext.SchemaName))
            .UseBigIntegerMoney()
            .Options;

        return new SweepDbContext(options);
    }
}
