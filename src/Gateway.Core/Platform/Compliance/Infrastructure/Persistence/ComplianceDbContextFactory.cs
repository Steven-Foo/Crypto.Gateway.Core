using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

/// <summary>Design-time only: lets <c>dotnet ef</c> build the context without booting a host.</summary>
public sealed class ComplianceDbContextFactory : IDesignTimeDbContextFactory<ComplianceDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public ComplianceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<ComplianceDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", ComplianceDbContext.SchemaName))
            .Options;

        return new ComplianceDbContext(options);
    }
}
