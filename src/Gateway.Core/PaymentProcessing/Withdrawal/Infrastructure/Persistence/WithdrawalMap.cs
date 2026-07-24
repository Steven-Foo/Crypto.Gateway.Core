using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalMap : IEntityTypeConfiguration<WithdrawalEntity>
{
    public void Configure(EntityTypeBuilder<WithdrawalEntity> builder)
    {
        builder.ToTable("Withdrawal");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.MerchantId).IsRequired();
        builder.Property(w => w.AssetId).IsRequired();
        builder.Property(w => w.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.DestinationAddress).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units.
        builder.Property(w => w.Amount).IsRequired();
        builder.Property(w => w.Fee).IsRequired();

        builder.Property(w => w.IdempotencyKey).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.ApprovedBy).HasMaxLength(128);
        builder.Property(w => w.SigningRequestId);

        // The signed, broadcast-ready transaction blob (public, not key material). varbinary(max), nullable.
        builder.Property(w => w.SignedTransaction);

        builder.Property(w => w.TransactionHash).IsUnicode(false).HasMaxLength(128);
        builder.Property(w => w.FailureReason).HasMaxLength(512);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.HasSignedTransaction);
        builder.Ignore(w => w.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotency arbiter (§7.3): one withdrawal per client key per merchant.
        builder.HasIndex(w => new { w.MerchantId, w.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_Withdrawal_Idempotency");

        // Workers' working set: withdrawals in a given status.
        builder.HasIndex(w => w.Status).HasDatabaseName("IX_Withdrawal_Status");
        builder.HasIndex(w => w.MerchantId).HasDatabaseName("IX_Withdrawal_Merchant");
    }
}
