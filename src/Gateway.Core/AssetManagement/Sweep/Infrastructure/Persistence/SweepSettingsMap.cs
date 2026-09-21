using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepSettingsMap : IEntityTypeConfiguration<SweepSettings>
{
    public void Configure(EntityTypeBuilder<SweepSettings> builder)
    {
        builder.ToTable("SweepSettings");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.Enabled).IsRequired();

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Base units (§14) — a threshold is money.
        builder.Property(s => s.MinSweepAmount).IsRequired();

        builder.Property(s => s.Confirmations).IsRequired();
        builder.Property(s => s.ScanIntervalMinutes).IsRequired();
        builder.Property(s => s.IsStaffConfigured).IsRequired();

        builder.Property(s => s.ScanRequestedAt);
        builder.Property(s => s.LastScanStartedAt);
        builder.Property(s => s.LastScanCompletedAt);
        builder.Property(s => s.LastSweepsCreated);

        builder.Property(s => s.UpdatedBy).HasMaxLength(128);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // Optimistic concurrency: two instances may claim the same due pass at the same moment, and the
        // loser must be told rather than silently overwrite the winner's "scan started" stamp.
        builder.Property<byte[]>("RowVersion").IsRowVersion();

        // One schedule per chain. Two rows would mean two cadences and two manual-request markers for the
        // same chain, with no way to say which one an operator was looking at.
        builder.HasIndex(s => s.Chain).IsUnique().HasDatabaseName("UX_SweepSettings_Chain");
    }
}
