using CryptoPaymentEngine.Gateway.Core.Blockchain.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Persistence;

public sealed class AssetMap : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("Asset");

        // Low-write reference table: a clustered GUID PK is fine, no Seq column needed.
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Symbol).HasMaxLength(16).IsRequired();
        builder.Property(a => a.ContractAddress).IsUnicode(false).HasMaxLength(128);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Decimals).IsRequired();

        builder.Ignore(a => a.IsNative);
        builder.Ignore(a => a.DomainEvents);

        // A chain may list the native coin (ContractAddress NULL) plus many tokens.
        // HasFilter(null) strips EF's default "[ContractAddress] IS NOT NULL" filter — without it
        // the native-coin rows escape the unique index entirely and (Tron, TRX, NULL) could be
        // inserted twice. SQL Server treats NULLs as equal for uniqueness, which is what we want.
        builder.HasIndex(a => new { a.Chain, a.Symbol, a.ContractAddress }).IsUnique().HasFilter(null);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Asset_Decimals_Range", "[Decimals] >= 0 AND [Decimals] <= 38"));
    }
}
