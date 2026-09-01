using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantRoleMap : IEntityTypeConfiguration<MerchantRole>
{
    public void Configure(EntityTypeBuilder<MerchantRole> builder)
    {
        builder.ToTable("MerchantRole");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        // Cross-module reference to the Merchant — an opaque Guid, no FK (§4.5).
        builder.Property(r => r.MerchantId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(256);
        builder.Property(r => r.PermissionCodesCsv).IsUnicode(false).HasMaxLength(2048);

        builder.Ignore(r => r.PermissionCodes);
        builder.Ignore(r => r.IsWildcard);
        builder.Ignore(r => r.DomainEvents);

        // Role names are unique WITHIN a tenant, not globally — two merchants may both have "Finance".
        builder.HasIndex(r => new { r.MerchantId, r.Name })
            .IsUnique()
            .HasDatabaseName("UX_MerchantRole_Merchant_Name");
    }
}
