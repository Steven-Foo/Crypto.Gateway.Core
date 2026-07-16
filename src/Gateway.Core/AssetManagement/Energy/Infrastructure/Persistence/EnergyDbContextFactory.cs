using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

/// <summary>Design-time only: lets <c>dotnet ef</c> build the context without booting a host.</summary>
public sealed class EnergyDbContextFactory : IDesignTimeDbContextFactory<EnergyDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public EnergyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<EnergyDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", EnergyDbContext.SchemaName))
            .UseBigIntegerMoney()
            .Options;

        return new EnergyDbContext(options);
    }
}
