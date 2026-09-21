using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

/// <summary>
/// Treasury's persistence: the watch-only cold treasury wallet registration. No outbox is used — nothing here
/// raises events or touches the ledger (§14) — but the base <see cref="ModuleDbContext"/> maps one for
/// consistency; it simply stays empty here.
/// </summary>
public sealed class TreasuryDbContext(DbContextOptions<TreasuryDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "treasury";

    public override string Schema => SchemaName;

    public DbSet<TreasuryColdWallet> ColdWallets => Set<TreasuryColdWallet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new TreasuryColdWalletMap());
    }
}
