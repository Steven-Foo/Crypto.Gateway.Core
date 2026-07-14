using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class DepositDbContext(DbContextOptions<DepositDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "deposit";

    public override string Schema => SchemaName;

    public DbSet<DepositEntity> Deposits => Set<DepositEntity>();
    public DbSet<ScanCursor> ScanCursors => Set<ScanCursor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new DepositMap());
        modelBuilder.ApplyConfiguration(new ScanCursorMap());
    }
}
