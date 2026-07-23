using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class DepositMap : IEntityTypeConfiguration<DepositEntity>
{
    public void Configure(EntityTypeBuilder<DepositEntity> builder)
    {
        builder.ToTable("Deposit");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Address).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(d => d.WalletId).IsRequired();
        builder.Property(d => d.MerchantId).IsRequired();
        builder.Property(d => d.AssetId).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units.
        builder.Property(d => d.Amount).IsRequired();

        builder.Property(d => d.TransactionHash).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(d => d.OutputIndex).IsRequired();
        builder.Property(d => d.BlockNumber).IsRequired();
        builder.Property(d => d.BlockHash).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Confirmations).IsRequired();

        // Null while the deposit is still watched for reorgs; set once its block is irreversible.
        builder.Property(d => d.FinalizedAt);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(d => d.IsConfirmed);
        builder.Ignore(d => d.IsPending);
        builder.Ignore(d => d.IsFinalized);
        builder.Ignore(d => d.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Deduplication arbiter (§7.3): one deposit per on-chain output, no matter how often re-scanned.
        builder.HasIndex(d => new { d.Chain, d.TransactionHash, d.OutputIndex })
            .IsUnique()
            .HasDatabaseName("UX_Deposit_Tx");

        // The confirmation tracker's working set: deposits on a chain still worth watching. Filtered to the
        // un-finalized ones so the index holds only live rows — the tracker's cost then tracks in-flight
        // deposits rather than every deposit ever taken (see DepositRepository.GetTrackableAsync).
        builder.HasIndex(d => new { d.Chain, d.Status })
            .HasFilter("[FinalizedAt] IS NULL")
            .HasDatabaseName("IX_Deposit_Chain_Status");

        // The pay page's "confirming" lookup (IDepositLookup) — polled frequently, keyed by address, not merchant.
        builder.HasIndex(d => new { d.Chain, d.Address, d.Status }).HasDatabaseName("IX_Deposit_Chain_Address_Status");

        builder.HasIndex(d => d.MerchantId).HasDatabaseName("IX_Deposit_Merchant");
    }
}
