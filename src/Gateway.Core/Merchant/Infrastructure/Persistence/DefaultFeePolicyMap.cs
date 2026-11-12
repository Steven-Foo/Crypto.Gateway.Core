using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class DefaultFeePolicyMap : IEntityTypeConfiguration<DefaultFeePolicy>
{
    public void Configure(EntityTypeBuilder<DefaultFeePolicy> builder)
    {
        builder.ToTable("DefaultFeePolicy");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        // BigInteger -> decimal(38,0) via BigIntegerTypeMapping (UseBigIntegerMoney), same as MerchantAssetPolicy.
        builder.Property(p => p.DepositFeeFixed).IsRequired();
        builder.Property(p => p.DepositFeeBps).IsRequired();
        builder.Property(p => p.MinimumDepositFee).IsRequired();
        builder.Property(p => p.WithdrawalFee).IsRequired();
        builder.Property(p => p.WithdrawalFeeBps).IsRequired();
        builder.Property(p => p.MinimumWithdrawalFee).IsRequired();

        // Platform-default limits. Nullable: null = no platform default for that bound (configuration applies).
        builder.Property(p => p.MinimumDeposit);
        builder.Property(p => p.MaximumDeposit);
        builder.Property(p => p.MinimumWithdrawal);
        builder.Property(p => p.MaximumWithdrawal);
        builder.Property(p => p.ApprovalThreshold);
        builder.Ignore(p => p.Fees);

        builder.Ignore(p => p.DomainEvents);

        // One default template per asset — no FK to blockchain.Asset (§4.5).
        builder.HasIndex(p => p.AssetId).IsUnique();

        // The domain enforces these too; the DB enforces them regardless of which code path writes.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_DefaultFeePolicy_NonNegative",
                "[DepositFeeFixed] >= 0 AND [WithdrawalFee] >= 0 AND [MinimumDepositFee] >= 0 AND [MinimumWithdrawalFee] >= 0");

            t.HasCheckConstraint(
                "CK_DefaultFeePolicy_Limits",
                "([MinimumDeposit] IS NULL OR [MinimumDeposit] >= 0) AND ([MaximumDeposit] IS NULL OR [MaximumDeposit] >= 0)"
                + " AND ([MinimumWithdrawal] IS NULL OR [MinimumWithdrawal] >= 0) AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)"
                + " AND ([ApprovalThreshold] IS NULL OR [ApprovalThreshold] >= 0)"
                + " AND ([MinimumDeposit] IS NULL OR [MaximumDeposit] IS NULL OR [MinimumDeposit] <= [MaximumDeposit])"
                + " AND ([MinimumWithdrawal] IS NULL OR [MaximumWithdrawal] IS NULL OR [MinimumWithdrawal] <= [MaximumWithdrawal])");

            t.HasCheckConstraint(
                "CK_DefaultFeePolicy_FeeBps",
                "[DepositFeeBps] >= 0 AND [DepositFeeBps] <= 10000 AND [WithdrawalFeeBps] >= 0 AND [WithdrawalFeeBps] <= 10000");
        });
    }
}
