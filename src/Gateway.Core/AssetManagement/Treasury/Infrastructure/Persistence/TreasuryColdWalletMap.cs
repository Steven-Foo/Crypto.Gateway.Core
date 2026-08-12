using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

public sealed class TreasuryColdWalletMap : IEntityTypeConfiguration<TreasuryColdWallet>
{
    public void Configure(EntityTypeBuilder<TreasuryColdWallet> builder)
    {
        builder.ToTable("TreasuryColdWallet");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Address).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(w => w.CreatedAt).IsRequired();
        builder.Property(w => w.UpdatedAt).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        // Exactly one cold treasury wallet per chain.
        builder.HasIndex(w => w.Chain).IsUnique().HasDatabaseName("UX_TreasuryColdWallet_Chain");
    }
}
