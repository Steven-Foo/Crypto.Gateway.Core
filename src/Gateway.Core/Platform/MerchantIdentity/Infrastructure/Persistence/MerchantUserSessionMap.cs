using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantUserSessionMap : IEntityTypeConfiguration<MerchantUserSession>
{
    public void Configure(EntityTypeBuilder<MerchantUserSession> builder)
    {
        builder.ToTable("MerchantUserSession");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.MerchantUserId).IsRequired();
        builder.Property(s => s.MerchantId).IsRequired();
        builder.Property(s => s.Username).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(s => s.TokenHash).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(s => s.CsrfToken).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(s => s.PermissionCodesCsv).IsUnicode(false).HasMaxLength(2048);

        builder.Ignore(s => s.PermissionCodes);
        builder.Ignore(s => s.DomainEvents);

        // Append-heavy: one row per login, non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // The validator's hot query: look up a presented token's hash.
        builder.HasIndex(s => s.TokenHash).IsUnique();
        builder.HasIndex(s => s.MerchantUserId);
    }
}
