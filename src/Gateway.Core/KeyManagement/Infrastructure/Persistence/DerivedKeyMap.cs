using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;

public sealed class DerivedKeyMap : IEntityTypeConfiguration<DerivedKey>
{
    public void Configure(EntityTypeBuilder<DerivedKey> builder)
    {
        builder.ToTable("DerivedKey");

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();

        builder.Property(k => k.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(k => k.Address).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(k => k.DerivationPath).IsUnicode(false).HasMaxLength(80).IsRequired();
        builder.Property(k => k.DerivationIndex).IsRequired();

        builder.Ignore(k => k.DomainEvents);

        // Append-heavy: one row per address ever handed out.
        builder.HasSeqClusteredIndex();

        // The invariant that protects customer funds. Two DerivedKey rows sharing an index would
        // mean two merchants share a deposit address; the database refuses, whatever the app does.
        builder.HasIndex(k => new { k.HdWalletId, k.DerivationIndex }).IsUnique();
        builder.HasIndex(k => new { k.Chain, k.Address }).IsUnique();

        builder.HasOne<HdWallet>()
            .WithMany()
            .HasForeignKey(k => k.HdWalletId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_DerivedKey_DerivationIndex_Range",
            $"[DerivationIndex] >= 0 AND [DerivationIndex] <= {DerivationPath.MaxIndex}"));
    }
}
