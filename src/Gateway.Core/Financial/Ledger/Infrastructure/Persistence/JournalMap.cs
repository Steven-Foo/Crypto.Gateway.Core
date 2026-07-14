using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

public sealed class JournalMap : IEntityTypeConfiguration<Journal>
{
    public void Configure(EntityTypeBuilder<Journal> builder)
    {
        builder.ToTable("Journal");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.ReferenceType).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(j => j.ReferenceId).IsRequired();
        builder.Property(j => j.AssetId).IsRequired();
        builder.Property(j => j.MerchantId); // null for platform-internal events
        builder.Property(j => j.Description).HasMaxLength(512).IsRequired();

        builder.Ignore(j => j.DomainEvents);

        // Append-heavy: non-clustered GUID PK, monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotent posting: one business event => exactly one journal, no matter how many deliveries.
        builder.HasIndex(j => new { j.ReferenceType, j.ReferenceId })
            .IsUnique()
            .HasDatabaseName("UX_Journal_Reference");

        // Merchant statements / ops period checks filter journals directly by merchant + time.
        builder.HasIndex(j => new { j.MerchantId, j.CreatedAt })
            .HasFilter("[MerchantId] IS NOT NULL")
            .HasDatabaseName("IX_Journal_Merchant_CreatedAt");

        builder.Metadata
            .FindNavigation(nameof(Journal.Entries))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(j => j.Entries)
            .WithOne()
            .HasForeignKey(e => e.JournalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
