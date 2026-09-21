using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

public sealed class AddressScreeningMap : IEntityTypeConfiguration<AddressScreening>
{
    public void Configure(EntityTypeBuilder<AddressScreening> builder)
    {
        builder.ToTable("AddressScreening");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();

        // varchar, not nvarchar: chain addresses are ASCII (§7.2). 128 comfortably covers Base58 TRON,
        // hex EVM and Base58 Solana without inviting an unbounded column.
        builder.Property(e => e.Address).IsUnicode(false).HasMaxLength(128).IsRequired();

        builder.Property(e => e.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Provider).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(e => e.Score);
        builder.Property(e => e.RiskLevel).HasMaxLength(16);
        builder.Property(e => e.ReasonsCsv).HasMaxLength(1024).IsRequired();
        builder.Property(e => e.AddressLabel).HasMaxLength(256);
        builder.Property(e => e.ReportUrl).IsUnicode(false).HasMaxLength(512);

        // The provider's verbatim payload. Unbounded on purpose: a truncated evidence blob is worse than
        // none, because it still looks complete while no longer matching what the vendor actually returned.
        builder.Property(e => e.RawResponse);

        builder.Property(e => e.FailureReason).HasMaxLength(512);
        builder.Property(e => e.PolicyDescription).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ScreenedAt).IsRequired();
        builder.Property(e => e.FreshUntil);

        builder.Ignore(e => e.DomainEvents);

        // Append-only evidence: every write is an insert and no read is by PK alone (§ persistence rules).
        builder.HasSeqClusteredIndex();

        // The hot path is "most recent screening for this address on this chain", so the index carries the
        // ordering as well as the filter. Without ScreenedAt descending in the key, every cache probe
        // becomes a sort over that address's whole history — which grows precisely for the addresses that
        // get screened most often.
        builder.HasIndex(e => new { e.Chain, e.Address, e.ScreenedAt })
            .HasDatabaseName("IX_AddressScreening_Chain_Address_ScreenedAt")
            .IsDescending(false, false, true);

        // Ops read: "show everything that blocked or needs review", newest first.
        builder.HasIndex(e => new { e.Decision, e.ScreenedAt })
            .HasDatabaseName("IX_AddressScreening_Decision_ScreenedAt")
            .IsDescending(false, true);
    }
}
