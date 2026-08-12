using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SweepEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain.Sweep;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepMap : IEntityTypeConfiguration<SweepEntity>
{
    public void Configure(EntityTypeBuilder<SweepEntity> builder)
    {
        builder.ToTable("Sweep");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.WalletId).IsRequired();
        builder.Property(s => s.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.AssetId).IsRequired();
        builder.Property(s => s.FromAddress).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(s => s.ToAddress).IsUnicode(false).HasMaxLength(128).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units (§14).
        builder.Property(s => s.Amount).IsRequired();

        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.SigningRequestId);

        // The signed, broadcast-ready transaction blob (public, not key material). varbinary(max), nullable.
        builder.Property(s => s.SignedTransaction);

        builder.Property(s => s.TransactionHash).IsUnicode(false).HasMaxLength(128);
        builder.Property(s => s.FailureReason).HasMaxLength(512);
        builder.Property(s => s.Confirmations);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(s => s.HasSignedTransaction);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotency arbiter (§7.3): at most one in-flight sweep per (wallet, asset). A terminal sweep
        // (Confirmed/Failed) is outside the filter, so the same address can be swept again later.
        builder.HasIndex(s => new { s.WalletId, s.AssetId })
            .IsUnique()
            .HasFilter("[Status] IN ('Pending', 'Signing', 'Broadcast')")
            .HasDatabaseName("UX_Sweep_InFlight_Wallet_Asset");

        // Workers' working set: sweeps in a given status.
        builder.HasIndex(s => s.Status).HasDatabaseName("IX_Sweep_Status");
    }
}
