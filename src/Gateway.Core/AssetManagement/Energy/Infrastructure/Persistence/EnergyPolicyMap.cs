using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

public sealed class EnergyPolicyMap : IEntityTypeConfiguration<EnergyPolicy>
{
    public void Configure(EntityTypeBuilder<EnergyPolicy> builder)
    {
        builder.ToTable("EnergyPolicy");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Chain).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.WalletType).IsUnicode(false).HasMaxLength(24).IsRequired();

        // BigInteger energy thresholds → decimal(38,0) via the money type-mapping plugin (context uses
        // .UseBigIntegerMoney()). Energy is a base-unit integer quantity, not a display value (§7.2).
        builder.Property(p => p.MinimumEnergy).IsRequired();
        builder.Property(p => p.TargetEnergy).IsRequired();
        builder.Property(p => p.StakeThreshold).IsRequired();
        builder.Property(p => p.RentalThreshold).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(p => p.DomainEvents);

        // One active policy per wallet type per chain — the monitor resolves a single policy.
        builder.HasIndex(p => new { p.Chain, p.WalletType }).IsUnique();
    }
}
