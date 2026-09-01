using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalDbContext(DbContextOptions<WithdrawalDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "withdrawal";

    public override string Schema => SchemaName;

    public DbSet<WithdrawalEntity> Withdrawals => Set<WithdrawalEntity>();

    /// <summary>Records of company funds moved into hot withdrawal wallets to keep the payout pipeline funded.
    /// Homed here rather than in Treasury because the pipeline that consumes the float lives in this module.</summary>
    public DbSet<HotWalletTopUp> HotWalletTopUps => Set<HotWalletTopUp>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new WithdrawalMap());
        modelBuilder.ApplyConfiguration(new HotWalletTopUpMap());
    }
}
