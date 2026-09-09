using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>A merchant's per-asset <b>deposit (payin)</b> min/max override, in base units. Null on a bound
/// means unset — <c>Minimum</c> falls back to the platform's per-chain dust-floor config, <c>Maximum</c> stays
/// unbounded (no platform-wide default exists for it today).</summary>
public sealed record MerchantDepositLimits(BigInteger? Minimum, BigInteger? Maximum)
{
    /// <summary>Neither bound overridden.</summary>
    public static readonly MerchantDepositLimits None = new(null, null);
}

/// <summary>
/// The read seam PaymentIntent consumes to gate an invoice's requested amount against a merchant's own
/// deposit limits, on top of the platform default (§4.5). A merchant with no policy for the asset returns
/// <see cref="MerchantDepositLimits.None"/> (all platform default).
/// </summary>
public interface IMerchantDepositLimits
{
    Task<MerchantDepositLimits> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default);
}
