using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepDbContext(DbContextOptions<SweepDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "sweep";

    public override string Schema => SchemaName;

    public DbSet<Domain.Sweep> Sweeps => Set<Domain.Sweep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new SweepMap());
    }
}
