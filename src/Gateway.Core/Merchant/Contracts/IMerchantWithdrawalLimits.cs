using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>A merchant's per-asset <b>user-withdrawal</b> min/max override, in base units. Null on a bound means
/// unset ⇒ the withdrawal flow uses the platform config limit for that bound. Distinct from the cash-out cap
/// (<see cref="IMerchantWithdrawalCap"/>).</summary>
public sealed record MerchantWithdrawalLimits(BigInteger? Minimum, BigInteger? Maximum)
{
    /// <summary>Neither bound overridden — the flow falls back entirely to the platform config limits.</summary>
    public static readonly MerchantWithdrawalLimits None = new(null, null);
}

/// <summary>
/// The read seam the Withdrawal module consumes to apply a merchant's own user-withdrawal limits over the
/// platform config default (§4.5). A merchant with no policy for the asset returns
/// <see cref="MerchantWithdrawalLimits.None"/> (all config).
/// </summary>
public interface IMerchantWithdrawalLimits
{
    Task<MerchantWithdrawalLimits> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default);
}
