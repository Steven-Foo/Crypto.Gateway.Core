using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

/// <summary>Design-time only: lets <c>dotnet ef</c> build the context without booting a host.</summary>
public sealed class MerchantIdentityDbContextFactory : IDesignTimeDbContextFactory<MerchantIdentityDbContext>
{
    public const string DefaultLocalConnection =
        @"Server=(localdb)\MSSQLLocalDB;Database=CryptoPaymentEngine;Trusted_Connection=True;TrustServerCertificate=True";

    public MerchantIdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CPE_DB_CONNECTION") ?? DefaultLocalConnection;

        var options = new DbContextOptionsBuilder<MerchantIdentityDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", MerchantIdentityDbContext.SchemaName))
            .Options;

        return new MerchantIdentityDbContext(options);
    }
}
