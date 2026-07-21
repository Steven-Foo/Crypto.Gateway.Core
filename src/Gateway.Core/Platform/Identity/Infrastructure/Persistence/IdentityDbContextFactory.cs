using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

/// <summary>Design-time only: lets <c>dotnet ef</c> build the context without booting a host.</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", IdentityDbContext.SchemaName))
            .Options;

        return new IdentityDbContext(options);
    }
}
