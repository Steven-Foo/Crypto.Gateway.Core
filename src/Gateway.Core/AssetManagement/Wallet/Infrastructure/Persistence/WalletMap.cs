using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

using WalletEntity = Domain.Wallet;

public sealed class WalletMap : IEntityTypeConfiguration<WalletEntity>
{
    public void Configure(EntityTypeBuilder<WalletEntity> builder)
    {
        builder.ToTable("Wallet");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Address).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(w => w.WalletType).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(256);
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.IsActive);
        builder.Ignore(w => w.IsMerchantAssignable);
        builder.Ignore(w => w.ActiveAssignment);
        builder.Ignore(w => w.DomainEvents);

        builder.HasIndex(w => new { w.Chain, w.Address }).IsUnique();
        // One Wallet per derived key: a key is never wrapped by two wallet rows.
        builder.HasIndex(w => w.DerivedKeyId).IsUnique();
        builder.HasIndex(w => w.MerchantId).HasFilter("[MerchantId] IS NOT NULL");

        builder.Metadata
            .FindNavigation(nameof(WalletEntity.Assignments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(w => w.Assignments)
            .WithOne()
            .HasForeignKey(a => a.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
