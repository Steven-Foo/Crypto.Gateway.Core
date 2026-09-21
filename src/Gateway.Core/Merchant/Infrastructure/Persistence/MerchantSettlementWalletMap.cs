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
        builder.Property(w => w.Label).HasMaxLength(128);
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.IsActive);

        // One ACTIVE settlement wallet per (merchant, chain) — the address that chain's cash-outs are paid
        // to. Several may be on file; the database is what guarantees only one of them is in use, so "where
        // do this merchant's earnings go" can never have two answers.
        builder.HasIndex(w => new { w.MerchantId, w.Chain })
            .IsUnique()
            .HasFilter("[Status] = 'Active'")
            .HasDatabaseName("UX_MerchantSettlementWallet_MerchantId_Chain_Active");

        // The same address is whitelisted once per (merchant, chain): a duplicate row is two records of one
        // approval, with nothing to say which one a later change applied to.
        builder.HasIndex(w => new { w.MerchantId, w.Chain, w.Address })
            .IsUnique()
            .HasDatabaseName("UX_MerchantSettlementWallet_MerchantId_Chain_Address");
    }
}
