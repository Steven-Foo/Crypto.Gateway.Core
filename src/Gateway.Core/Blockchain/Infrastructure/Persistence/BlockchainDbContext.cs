using CryptoPaymentEngine.Gateway.Core.Blockchain.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Persistence;

public sealed class BlockchainDbContext(DbContextOptions<BlockchainDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "blockchain";

    public override string Schema => SchemaName;

    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new AssetMap());
    }
}
