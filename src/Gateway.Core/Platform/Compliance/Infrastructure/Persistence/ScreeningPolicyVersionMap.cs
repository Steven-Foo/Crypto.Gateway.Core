using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

public sealed class ScreeningPolicyVersionMap : IEntityTypeConfiguration<ScreeningPolicyVersion>
{
    public void Configure(EntityTypeBuilder<ScreeningPolicyVersion> builder)
    {
        builder.ToTable("ScreeningPolicyVersion");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.BlockScore).IsRequired();
        builder.Property(e => e.ReviewScore).IsRequired();
        builder.Property(e => e.CacheDays).IsRequired();
        builder.Property(e => e.IndirectReviewMaxHops).IsRequired();

        // A share of volume, not money — so a scaled decimal is correct here, unlike an amount (§14 governs
        // amounts, which stay integer base units). Two places is finer than any provider reports.
        builder.Property(e => e.IndirectReviewMinPercent).HasColumnType("decimal(5,2)").IsRequired();

        builder.Property(e => e.ExtraAlwaysBlockIndicatorsCsv).HasMaxLength(1024).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(512);
        builder.Property(e => e.UpdatedBy).HasMaxLength(128).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.Ignore(e => e.DomainEvents);

        // Append-only: every change is an insert, and the only read is "the newest one".
        builder.HasSeqClusteredIndex();

        builder.HasIndex(e => e.UpdatedAt)
            .HasDatabaseName("IX_ScreeningPolicyVersion_UpdatedAt")
            .IsDescending(true);
    }
}
