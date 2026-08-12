using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

public sealed class TreasuryReloadMap : IEntityTypeConfiguration<TreasuryReload>
{
    public void Configure(EntityTypeBuilder<TreasuryReload> builder)
    {
        builder.ToTable("TreasuryReload");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.AssetId).IsRequired();
        builder.Property(r => r.SourceAddress).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(r => r.TargetWalletId).IsRequired();
        builder.Property(r => r.TargetAddress).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units (§14).
        builder.Property(r => r.Amount).IsRequired();

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(24).IsRequired();

        // The unsigned tx the operator signs client-side + the signed blob the backend broadcasts. Public,
        // never key material (§10). varbinary(max).
        builder.Property(r => r.UnsignedPayload).IsRequired();
        builder.Property(r => r.SignedTransaction);

        builder.Property(r => r.TransactionHash).IsUnicode(false).HasMaxLength(128);
        builder.Property(r => r.Confirmations);
        builder.Property(r => r.StatusReason).HasMaxLength(512);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(r => r.HasSignedTransaction);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Workers' working set: reloads in a given status.
        builder.HasIndex(r => r.Status).HasDatabaseName("IX_TreasuryReload_Status");
    }
}
