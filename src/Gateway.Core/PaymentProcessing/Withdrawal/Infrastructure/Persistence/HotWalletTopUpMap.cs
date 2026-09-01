using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class HotWalletTopUpMap : IEntityTypeConfiguration<HotWalletTopUp>
{
    public void Configure(EntityTypeBuilder<HotWalletTopUp> builder)
    {
        builder.ToTable("HotWalletTopUp");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.AssetId).IsRequired();

        // The hot-pool wallet that received the funds. A cross-module reference by opaque Guid — no FK, so
        // Wallet stays independently extractable (§4.5).
        builder.Property(t => t.TargetWalletId).IsRequired();
        builder.Property(t => t.TargetAddress).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units.
        builder.Property(t => t.Amount).IsRequired();

        builder.Property(t => t.TransactionHash).IsUnicode(false).HasMaxLength(128).IsRequired();

        // The company wallet the funds came from — outside platform custody, recorded for audit only.
        builder.Property(t => t.SourceAddress).IsUnicode(false).HasMaxLength(128);

        builder.Property(t => t.RecordedBy).HasMaxLength(128).IsRequired();
        builder.Property(t => t.RecordedAt).IsRequired();

        builder.Ignore(t => t.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // One record per on-chain transfer. Recording the same hash twice would credit custody twice for a
        // single real transfer, inflating TreasuryAsset against a chain that never moved again — the DB is
        // the arbiter (§7.3), never a prior read in application code.
        builder.HasIndex(t => t.TransactionHash)
            .IsUnique()
            .HasDatabaseName("UX_HotWalletTopUp_TxHash");

        // The ops activity screen reads newest-first per chain.
        builder.HasIndex(t => new { t.Chain, t.RecordedAt }).HasDatabaseName("IX_HotWalletTopUp_Chain_RecordedAt");
    }
}
