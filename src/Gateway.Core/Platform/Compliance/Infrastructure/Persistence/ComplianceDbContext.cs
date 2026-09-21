using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "compliance";

    public override string Schema => SchemaName;

    public DbSet<AddressScreening> AddressScreenings => Set<AddressScreening>();

    public DbSet<ScreeningPolicyVersion> ScreeningPolicyVersions => Set<ScreeningPolicyVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new AddressScreeningMap());
        modelBuilder.ApplyConfiguration(new ScreeningPolicyVersionMap());
    }
}
