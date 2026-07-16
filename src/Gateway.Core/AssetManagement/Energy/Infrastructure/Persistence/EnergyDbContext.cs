using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

public sealed class EnergyDbContext(DbContextOptions<EnergyDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "energy";

    public override string Schema => SchemaName;

    public DbSet<EnergyPolicy> EnergyPolicies => Set<EnergyPolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new EnergyPolicyMap());
    }
}
