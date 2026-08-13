using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

public sealed class EnergyOperationMap : IEntityTypeConfiguration<EnergyOperation>
{
    public void Configure(EntityTypeBuilder<EnergyOperation> builder)
    {
        builder.ToTable("EnergyOperation");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.StakingWalletId).IsRequired();
        builder.Property(o => o.OwnerAddress).IsUnicode(false).HasMaxLength(128).IsRequired();
        builder.Property(o => o.TargetAddress).IsUnicode(false).HasMaxLength(128);

        // BigInteger -> decimal(38,0) via UseBigIntegerMoney. Unsigned base units (sun) (§14).
        builder.Property(o => o.AmountSun).IsRequired();

        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.SigningRequestId);
        builder.Property(o => o.SignedTransaction);
        builder.Property(o => o.TransactionHash).IsUnicode(false).HasMaxLength(128);
        builder.Property(o => o.FailureReason).HasMaxLength(512);
        builder.Property(o => o.Confirmations);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(o => o.HasSignedTransaction);

        // Append-heavy: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        // Idempotency (§7.3): at most one in-flight Stake per staking wallet.
        builder.HasIndex(o => o.StakingWalletId)
            .IsUnique()
            .HasFilter("[Kind] = 'Stake' AND [Status] IN ('Pending', 'Signing', 'Broadcast')")
            .HasDatabaseName("UX_EnergyOp_InFlight_Stake");

        // Idempotency (§7.3): at most one in-flight Delegate per (chain, target address).
        builder.HasIndex(o => new { o.Chain, o.TargetAddress })
            .IsUnique()
            .HasFilter("[Kind] = 'Delegate' AND [Status] IN ('Pending', 'Signing', 'Broadcast')")
            .HasDatabaseName("UX_EnergyOp_InFlight_Delegate");

        // Idempotency (§7.3): at most one in-flight TopUp per (chain, target address) — never two TRX top-ups
        // racing to the same short address. The NAMED-index overload is REQUIRED here: EF identifies an index by
        // its property set, so a second index on the SAME columns as the Delegate index above would silently
        // REPLACE it; a named index coexists with the unnamed one instead of overwriting it.
        builder.HasIndex(["Chain", "TargetAddress"], "UX_EnergyOp_InFlight_TopUp")
            .IsUnique()
            .HasFilter("[Kind] = 'TopUp' AND [Status] IN ('Pending', 'Signing', 'Broadcast')");

        // Workers' working set: operations in a given status.
        builder.HasIndex(o => o.Status).HasDatabaseName("IX_EnergyOp_Status");
    }
}
