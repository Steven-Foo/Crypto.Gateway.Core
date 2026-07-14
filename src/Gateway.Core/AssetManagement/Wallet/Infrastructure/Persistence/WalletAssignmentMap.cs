using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

public sealed class WalletAssignmentMap : IEntityTypeConfiguration<WalletAssignment>
{
    public void Configure(EntityTypeBuilder<WalletAssignment> builder)
    {
        builder.ToTable("WalletAssignment");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Ignore(a => a.IsActive);
        builder.Ignore(a => a.DomainEvents);

        // Append-heavy history: non-clustered GUID PK + monotonic clustered Seq.
        builder.HasSeqClusteredIndex();

        builder.HasIndex(a => a.MerchantId);

        // The invariant: at most one Active assignment per wallet. The DB enforces it, so no race
        // between "check none active" and "insert" can double-assign an address.
        builder.HasIndex(a => a.WalletId)
            .IsUnique()
            .HasFilter("[Status] = 'Active'");
    }
}
