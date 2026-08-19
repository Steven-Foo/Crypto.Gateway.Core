using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;

/// <summary>
/// The platform-default fee applied to a merchant with NO explicit pricing for an asset, so an unpriced
/// merchant is not silently free. Percentage-only (basis points), deliberately: a flat component is per-asset
/// (base units depend on the asset's decimals), so a platform-wide flat default is meaningless — per-asset flat
/// defaults are a follow-up. Zero on both (the default) ⇒ no platform default (unpriced merchants stay free).
/// Account opening is unaffected — there is no onboarding fee anywhere in the system.
/// </summary>
public sealed class MerchantDefaultFeeOptions
{
    public const string SectionName = "Merchant:DefaultFee";

    /// <summary>Default deposit percentage in basis points (1bp = 0.01%). Must be &lt; 10000 (100%).</summary>
    public int DepositFeeBps { get; init; }

    /// <summary>Default withdrawal percentage in basis points. Must be ≤ 10000 (100%).</summary>
    public int WithdrawalFeeBps { get; init; }
}

/// <summary>
/// Holds the resolved platform-default <see cref="FeeSchedule"/> — built once from config — so the per-request
/// fee resolver just substitutes it for an unpriced merchant. Invalid or all-zero config ⇒
/// <see cref="FeeSchedule.None"/> (no default; unpriced merchants stay free).
/// </summary>
public sealed class MerchantDefaultFee
{
    public FeeSchedule Schedule { get; }

    public MerchantDefaultFee(IOptions<MerchantDefaultFeeOptions> options, ILogger<MerchantDefaultFee> logger)
    {
        var o = options.Value;
        if (o.DepositFeeBps == 0 && o.WithdrawalFeeBps == 0)
        {
            Schedule = FeeSchedule.None;
            return;
        }

        var created = FeeSchedule.Create(BigInteger.Zero, o.DepositFeeBps, BigInteger.Zero, o.WithdrawalFeeBps);
        if (created.IsFailure)
        {
            logger.LogWarning(
                "Merchant:DefaultFee is invalid ({Error}) — falling back to no platform default fee.",
                created.Error!.Message);
            Schedule = FeeSchedule.None;
            return;
        }

        Schedule = created.Value;
    }
}
