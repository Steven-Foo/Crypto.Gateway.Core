using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantConfigurationMap : IEntityTypeConfiguration<MerchantConfiguration>
{
    public void Configure(EntityTypeBuilder<MerchantConfiguration> builder)
    {
        builder.ToTable("MerchantConfiguration");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(c => c.DomainEvents);

        builder.HasIndex(c => c.MerchantId).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_MerchantConfiguration_WebhookRetryCount",
            $"[WebhookRetryCount] >= 0 AND [WebhookRetryCount] <= {MerchantConfiguration.MaxWebhookRetryCount}"));
    }
}
