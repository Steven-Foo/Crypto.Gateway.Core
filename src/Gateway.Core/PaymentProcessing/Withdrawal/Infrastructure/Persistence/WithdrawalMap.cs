using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
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

        // User payout vs merchant earnings cash-out. Default 'User' — every pre-existing row is a user payout.
        builder.Property(w => w.Kind).HasConversion<string>().HasMaxLength(16).IsRequired().HasDefaultValueSql("'User'");

        builder.Property(w => w.DestinationAddress).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units.
        builder.Property(w => w.Amount).IsRequired();
        builder.Property(w => w.Fee).IsRequired();

        builder.Property(w => w.MerchantTransactionId).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(w => w.CallbackUrl).HasMaxLength(512);
        // 24, not 16: the funding-hold statuses ("AwaitingFunds"/"AwaitingRelease") are longer than the
        // original lifecycle names, and the extra headroom keeps future statuses from silently truncating.
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(w => w.ApprovedBy).HasMaxLength(128);
        builder.Property(w => w.SigningRequestId);

        // Why the withdrawal is parked (ops trace) + who/when released a large one for send.
        builder.Property(w => w.StatusReason).HasMaxLength(512);
        builder.Property(w => w.ReleasedBy).HasMaxLength(128);
        builder.Property(w => w.ReleasedAt);

        // The hot-pool wallet this payout is sent from (leased until confirmed). Null until signed.
        builder.Property(w => w.SourceWalletId);

        // The signed, broadcast-ready transaction blob (public, not key material). varbinary(max), nullable.
        builder.Property(w => w.SignedTransaction);

        builder.Property(w => w.TransactionHash).IsUnicode(false).HasMaxLength(128);
        builder.Property(w => w.FailureReason).HasMaxLength(512);
        builder.Property(w => w.Confirmations);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.HasSignedTransaction);
        builder.Ignore(w => w.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotency arbiter (§7.3): one withdrawal per (merchant, kind, merchant transaction id) — a resent
        // reference is rejected, never paid twice. Kind is in the key so user payouts and merchant cash-outs
        // have SEPARATE id spaces (a merchant may reuse a reference across the two without colliding); the id is
        // unique per merchant, not globally.
        builder.HasIndex(w => new { w.MerchantId, w.Kind, w.MerchantTransactionId })
            .IsUnique()
            .HasDatabaseName("UX_Withdrawal_MerchantTxn");

        // Workers' working set: withdrawals in a given status.
        builder.HasIndex(w => w.Status).HasDatabaseName("IX_Withdrawal_Status");
        builder.HasIndex(w => w.MerchantId).HasDatabaseName("IX_Withdrawal_Merchant");

        // One in-flight withdrawal per hot-pool wallet: a wallet is leased from sign until confirm, so at most
        // one Signing/Broadcast withdrawal may carry a given SourceWalletId. The filtered unique index is the
        // DB-level arbiter (a lost allocation race retries with another wallet), mirroring the Sweep pattern.
        builder.HasIndex(w => w.SourceWalletId)
            .IsUnique()
            .HasFilter("[Status] IN ('Signing', 'Broadcast')")
            .HasDatabaseName("UX_Withdrawal_InFlight_SourceWallet");
    }
}
