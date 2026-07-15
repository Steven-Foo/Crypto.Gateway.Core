using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

public sealed class PaymentIntentMap : IEntityTypeConfiguration<PaymentIntentEntity>
{
    public void Configure(EntityTypeBuilder<PaymentIntentEntity> builder)
    {
        builder.ToTable("PaymentIntent");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.PublicReference).IsRequired();
        builder.Property(i => i.MerchantId).IsRequired();
        builder.Property(i => i.MerchantTransactionId).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(i => i.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.AssetId).IsRequired();
        builder.Property(i => i.WalletId).IsRequired();
        builder.Property(i => i.Address).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units.
        builder.Property(i => i.ExpectedAmount).IsRequired();

        builder.Property(i => i.CallbackUrl).HasMaxLength(512);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.MatchedDepositId);
        builder.Property(i => i.AmountMatched);
        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Property(i => i.UpdatedAt).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(i => i.IsWaiting);
        builder.Ignore(i => i.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotency arbiter (§7.3): one invoice per merchant transaction reference.
        builder.HasIndex(i => new { i.MerchantId, i.MerchantTransactionId })
            .IsUnique()
            .HasDatabaseName("UX_PaymentIntent_Idempotency");

        // Public pay-page lookup.
        builder.HasIndex(i => i.PublicReference)
            .IsUnique()
            .HasDatabaseName("UX_PaymentIntent_PublicRef");

        // Reservation arbiter: at most one WAITING invoice may hold a given address. The expiry sweep flips
        // lapsed invoices out of Waiting, so an address becomes reusable once free.
        builder.HasIndex(i => i.WalletId)
            .IsUnique()
            .HasFilter("[Status] = 'Waiting'")
            .HasDatabaseName("UX_PaymentIntent_LiveWallet");

        // Match-idempotency lookup and the expiry sweep's working set.
        builder.HasIndex(i => i.MatchedDepositId).HasDatabaseName("IX_PaymentIntent_MatchedDeposit");
        builder.HasIndex(i => new { i.Status, i.ExpiresAt }).HasDatabaseName("IX_PaymentIntent_Status_Expiry");
    }
}
