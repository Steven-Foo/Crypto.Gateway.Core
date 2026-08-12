using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

public sealed class KeyManagementDbContext(DbContextOptions<KeyManagementDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "keymgmt";

    public override string Schema => SchemaName;

    public DbSet<HdWallet> HdWallets => Set<HdWallet>();
    public DbSet<DerivedKey> DerivedKeys => Set<DerivedKey>();

    /// <summary>KMS-envelope material (production custody): sealed seed + public xpub per HD wallet (§10).</summary>
    public DbSet<SecretMaterial> SecretMaterials => Set<SecretMaterial>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new HdWalletMap());
        modelBuilder.ApplyConfiguration(new DerivedKeyMap());
        modelBuilder.ApplyConfiguration(new SecretMaterialMap());
    }
}
