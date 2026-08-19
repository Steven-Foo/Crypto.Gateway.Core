using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure.Persistence;

public sealed class AuditEntryMap : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntry");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.StaffUserId).IsRequired();
        builder.Property(e => e.StaffUsername).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(128).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(128);
        builder.Property(e => e.Reason).HasMaxLength(512);
        builder.Property(e => e.IpAddress).IsUnicode(false).HasMaxLength(64);

        builder.Ignore(e => e.DomainEvents);

        // Append-only, one row per mutation: non-clustered GUID PK + monotonic clustered Seq (§ persistence
        // rules) — every write is an insert, reads are always filtered/paged, never by PK alone.
        builder.HasSeqClusteredIndex();

        builder.HasIndex(e => e.StaffUserId);
        builder.HasIndex(e => e.Action);
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => e.CreatedAt);
    }
}
