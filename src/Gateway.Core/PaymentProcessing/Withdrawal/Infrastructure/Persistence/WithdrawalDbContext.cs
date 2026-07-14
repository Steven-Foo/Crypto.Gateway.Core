using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalDbContext(DbContextOptions<WithdrawalDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "withdrawal";

    public override string Schema => SchemaName;

    public DbSet<WithdrawalEntity> Withdrawals => Set<WithdrawalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new WithdrawalMap());
    }
}
