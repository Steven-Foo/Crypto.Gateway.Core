using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantMap : IEntityTypeConfiguration<Domain.Merchant>
{
    public void Configure(EntityTypeBuilder<Domain.Merchant> builder)
    {
        builder.ToTable("Merchant");

        // Low-write table: clustered GUID PK is fine, no Seq column.
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.MerchantCode).IsUnicode(false).HasMaxLength(Domain.Merchant.MaxCodeLength).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(256).IsRequired();
        builder.Property(m => m.CallbackUrl).HasMaxLength(512);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(m => m.CanTransact);
        builder.Ignore(m => m.DomainEvents);

        builder.HasIndex(m => m.MerchantCode).IsUnique();

        // Restrict, not Cascade: merchants are never deleted (status becomes Closed). A cascade
        // path here would make an accidental delete silently take credentials and policies with it.
        builder.HasOne(m => m.Configuration)
            .WithOne()
            .HasForeignKey<MerchantConfiguration>(c => c.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata
            .FindNavigation(nameof(Domain.Merchant.Credentials))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata
            .FindNavigation(nameof(Domain.Merchant.AssetPolicies))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(m => m.Credentials)
            .WithOne()
            .HasForeignKey(c => c.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(m => m.AssetPolicies)
            .WithOne()
            .HasForeignKey(p => p.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
