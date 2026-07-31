using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure.Persistence;

public sealed class CallbackDeliveryMap : IEntityTypeConfiguration<CallbackDelivery>
{
    public void Configure(EntityTypeBuilder<CallbackDelivery> builder)
    {
        builder.ToTable("CallbackDelivery");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.ReferenceType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.ReferenceId).IsRequired();

        // The exact signed request, persisted so every retry (automatic or manual) resends identically.
        builder.Property(c => c.CallbackUrl).HasMaxLength(2048).IsRequired();
        builder.Property(c => c.Body).IsRequired();
        builder.Property(c => c.CallbackType).IsUnicode(false).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Timestamp).IsUnicode(false).HasMaxLength(32).IsRequired();
        builder.Property(c => c.SignatureHex).IsUnicode(false).HasMaxLength(256).IsRequired();

        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.AttemptCount).IsRequired();
        builder.Property(c => c.LastError).HasMaxLength(512);

        builder.Ignore(c => c.DomainEvents);

        // Append-heavy: one row per transaction's callback, non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // One delivery record per transaction; also the Ops query's lookup path.
        builder.HasIndex(c => new { c.ReferenceType, c.ReferenceId })
            .IsUnique()
            .HasDatabaseName("UX_CallbackDelivery_Reference");

        // The worker's working set: rows due for another automatic attempt.
        builder.HasIndex(c => new { c.Status, c.NextAttemptAt }).HasDatabaseName("IX_CallbackDelivery_Status_NextAttemptAt");
    }
}
