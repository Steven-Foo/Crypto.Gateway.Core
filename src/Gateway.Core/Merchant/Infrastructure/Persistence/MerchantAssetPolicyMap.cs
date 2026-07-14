using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantAssetPolicyMap : IEntityTypeConfiguration<MerchantAssetPolicy>
{
    public void Configure(EntityTypeBuilder<MerchantAssetPolicy> builder)
    {
        builder.ToTable("MerchantAssetPolicy");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // BigInteger -> decimal(38,0) via BigIntegerTypeMapping (UseBigIntegerMoney).
        builder.Property(p => p.SweepThreshold).IsRequired();
        builder.Property(p => p.MinimumWithdrawal).IsRequired();
        builder.Property(p => p.MaximumWithdrawal); // null = no upper bound
        builder.Property(p => p.WithdrawalFee).IsRequired();

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(p => p.DomainEvents);

        // AssetId is an opaque cross-module reference — no FK to blockchain.Asset (§4.5).
        builder.HasIndex(p => new { p.MerchantId, p.AssetId }).IsUnique();

        // The domain enforces these too; the DB enforces them regardless of which code path writes.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_NonNegative",
                "[SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");

            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_WithdrawalRange",
                "[MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]");
        });
    }
}
