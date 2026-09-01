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
        builder.Property(p => p.MinimumWithdrawal); // null = unset (fall back to the platform config minimum)
        builder.Property(p => p.MaximumWithdrawal); // null = unset (fall back to the platform config maximum)

        // Pricing: fixed base-unit components + basis-point percentages (§14). Defaults keep existing
        // rows (and unpriced merchants) at zero fee.
        builder.Property(p => p.DepositFeeFixed).IsRequired().HasDefaultValueSql("0"); // BigInteger → decimal(38,0)
        builder.Property(p => p.DepositFeeBps).IsRequired().HasDefaultValue(0);
        builder.Property(p => p.WithdrawalFee).IsRequired();
        builder.Property(p => p.WithdrawalFeeBps).IsRequired().HasDefaultValue(0);

        // Merchant top-up pricing. Defaults to zero and, unlike deposit/withdrawal, never falls back to the
        // platform default fee — a merchant is not charged to fund its own float unless staff say so.
        builder.Property(p => p.TopUpFeeFixed).IsRequired().HasDefaultValueSql("0");
        builder.Property(p => p.TopUpFeeBps).IsRequired().HasDefaultValue(0);

        // Merchant-withdrawal (earnings cash-out) liquidity cap — distinct from the user Min/MaxWithdrawal.
        // Null flat + 0 bps = no cap. BigInteger? → decimal(38,0) nullable.
        builder.Property(p => p.MerchantWithdrawalFlatCap);
        builder.Property(p => p.MerchantWithdrawalPercentBps).IsRequired().HasDefaultValue(0);

        // Per-merchant approval-threshold override — null = unset (fall back to the platform config threshold).
        builder.Property(p => p.ApprovalThreshold);

        builder.Property<byte[]>("RowVersion").IsRowVersion();

        builder.Ignore(p => p.DomainEvents);

        // AssetId is an opaque cross-module reference — no FK to blockchain.Asset (§4.5).
        builder.HasIndex(p => new { p.MerchantId, p.AssetId }).IsUnique();

        // The domain enforces these too; the DB enforces them regardless of which code path writes.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_NonNegative",
                "[SweepThreshold] >= 0 AND [WithdrawalFee] >= 0 AND [DepositFeeFixed] >= 0 AND [TopUpFeeFixed] >= 0 AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)");

            // Every fee is deducted from what arrives, so all three rates share the same 0-100% bound.
            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_FeeBps",
                "[DepositFeeBps] >= 0 AND [DepositFeeBps] <= 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000 AND [TopUpFeeBps] >= 0 AND [TopUpFeeBps] <= 10000");

            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_WithdrawalRange",
                "[MaximumWithdrawal] IS NULL OR [MinimumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]");

            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_MerchantWithdrawalCap",
                "[MerchantWithdrawalPercentBps] >= 0 AND [MerchantWithdrawalPercentBps] <= 10000 AND ([MerchantWithdrawalFlatCap] IS NULL OR [MerchantWithdrawalFlatCap] >= 0)");

            t.HasCheckConstraint(
                "CK_MerchantAssetPolicy_ApprovalThreshold",
                "[ApprovalThreshold] IS NULL OR [ApprovalThreshold] >= 0");
        });
    }
}
