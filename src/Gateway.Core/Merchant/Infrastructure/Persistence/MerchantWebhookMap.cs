using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantWebhookMap : IEntityTypeConfiguration<MerchantWebhook>
{
    public void Configure(EntityTypeBuilder<MerchantWebhook> builder)
    {
        builder.ToTable("MerchantWebhook");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.EventType).HasMaxLength(64).IsRequired();
        builder.Property(w => w.Payload).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.LastResponse).HasMaxLength(MerchantWebhook.MaxResponseLength);

        // Mutable: retry workers update these rows concurrently (§1.4).
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.DomainEvents);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        builder.HasIndex(w => w.MerchantId);

        // The retry worker's hot query: what is due for redelivery?
        builder.HasIndex(w => new { w.Status, w.NextRetryAt })
            .HasFilter("[NextRetryAt] IS NOT NULL");
    }
}
