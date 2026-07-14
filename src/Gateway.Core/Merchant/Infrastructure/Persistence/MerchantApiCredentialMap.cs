using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantApiCredentialMap : IEntityTypeConfiguration<MerchantApiCredential>
{
    public void Configure(EntityTypeBuilder<MerchantApiCredential> builder)
    {
        builder.ToTable("MerchantApiCredential");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        // ApiKey is a public identifier (ASCII), safe to store and index in clear.
        builder.Property(c => c.ApiKey).IsUnicode(false).HasMaxLength(64).IsRequired();

        // HMAC-SHA256, base64. The secret itself is never stored — there is no column for it.
        builder.Property(c => c.SecretHash).IsUnicode(false).HasMaxLength(256).IsRequired();
        builder.Property(c => c.HashVersion).IsRequired();

        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Ignore(c => c.IsActive);
        builder.Ignore(c => c.DomainEvents);

        builder.HasIndex(c => c.ApiKey).IsUnique();

        // The authentication hot path: resolve an active credential by key.
        builder.HasIndex(c => new { c.MerchantId, c.Status });
    }
}
