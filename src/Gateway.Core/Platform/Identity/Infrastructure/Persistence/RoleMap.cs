using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class RoleMap : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Role");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Name).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(256);
        builder.Property(r => r.PermissionCodesCsv).IsUnicode(false).HasMaxLength(2048);

        builder.Ignore(r => r.PermissionCodes);
        builder.Ignore(r => r.IsWildcard);
        builder.Ignore(r => r.DomainEvents);

        builder.HasIndex(r => r.Name).IsUnique();
    }
}
