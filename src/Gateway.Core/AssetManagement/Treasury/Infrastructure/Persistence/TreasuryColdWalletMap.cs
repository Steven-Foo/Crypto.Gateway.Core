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
        builder.Property(w => w.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.Address).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(w => w.Label).HasMaxLength(128);

        // Snapshot of the latest screening, so the staff list renders without a cross-module join and
        // without spending provider quota. The evidence itself stays append-only in Compliance; this is a
        // denormalised copy of its most recent row, and ScreeningId is an opaque reference, not an FK (§4.5).
        builder.Property(w => w.ScreeningId);
        builder.Property(w => w.ScreeningDecision).IsUnicode(false).HasMaxLength(16);
        builder.Property(w => w.ScreeningScore);
        builder.Property(w => w.ScreenedAt);

        builder.Property(w => w.CreatedAt).IsRequired();
        builder.Property(w => w.UpdatedAt).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(w => w.IsActive);

        // One destination per (chain, kind): the clean sweep destination and the quarantine one. The DB is
        // the arbiter, not application logic — two active rows would make "where do sweeps go" ambiguous at
        // the exact moment it matters (§7.3).
        builder.HasIndex(w => new { w.Chain, w.Kind })
            .IsUnique()
            .HasFilter("[Status] = 'Active'")
            .HasDatabaseName("UX_TreasuryColdWallet_Chain_Kind_Active");

        // The same address must not be registered twice on a chain: the custody audit sums addresses, and a
        // duplicate row invites a second, contradictory answer to what that address is for.
        builder.HasIndex(w => new { w.Chain, w.Address })
            .IsUnique()
            .HasDatabaseName("UX_TreasuryColdWallet_Chain_Address");
    }
}
