using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepDbContext(DbContextOptions<SweepDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "sweep";

    public override string Schema => SchemaName;

    public DbSet<Domain.Sweep> Sweeps => Set<Domain.Sweep>();

    /// <summary>Per-chain sweep dials and schedule state — one row per configured chain.</summary>
    public DbSet<Domain.SweepSettings> SweepSettings => Set<Domain.SweepSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new SweepMap());
        modelBuilder.ApplyConfiguration(new SweepSettingsMap());
    }
}
