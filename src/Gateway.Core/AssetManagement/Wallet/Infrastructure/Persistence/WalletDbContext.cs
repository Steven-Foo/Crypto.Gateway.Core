using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

using WalletEntity = Domain.Wallet;

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "wallet";

    public override string Schema => SchemaName;

    public DbSet<WalletEntity> Wallets => Set<WalletEntity>();
    public DbSet<WalletAssignment> Assignments => Set<WalletAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new WalletMap());
        modelBuilder.ApplyConfiguration(new WalletAssignmentMap());
    }
}
