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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new HdWalletMap());
        modelBuilder.ApplyConfiguration(new DerivedKeyMap());
    }
}
