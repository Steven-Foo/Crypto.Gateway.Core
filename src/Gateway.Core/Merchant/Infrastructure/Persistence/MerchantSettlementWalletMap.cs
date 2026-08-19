using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantSettlementWalletMap : IEntityTypeConfiguration<MerchantSettlementWallet>
{
    public void Configure(EntityTypeBuilder<MerchantSettlementWallet> builder)
    {
        builder.ToTable("MerchantSettlementWallet");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.MerchantId).IsRequired();
        builder.Property(w => w.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Address).IsUnicode(false).HasMaxLength(128).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        // One settlement wallet per (merchant, chain) — the fixed cash-out destination for that chain.
        builder.HasIndex(w => new { w.MerchantId, w.Chain }).IsUnique();
    }
}
