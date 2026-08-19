using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// A merchant's per-asset <b>merchant-withdrawal (earnings cash-out) liquidity cap</b>. Distinct from the
/// user withdrawal min/max — this caps a single cash-out at <c>min(FlatCap, ⌊available·PercentBps/10000⌋)</c>.
/// A null flat cap and 0 bps means <b>no cap</b> (cash out up to the full available balance). Amounts are
/// base units (§14).
/// </summary>
public sealed record MerchantWithdrawalCap(BigInteger? FlatCap, int PercentBps)
{
    /// <summary>No cap configured — cash out up to the full available balance.</summary>
    public static MerchantWithdrawalCap None { get; } = new(null, 0);

    public bool HasCap => FlatCap is not null || PercentBps > 0;
}

/// <summary>Reads a merchant's cash-out liquidity cap for an asset. Unconfigured ⇒ <see cref="MerchantWithdrawalCap.None"/>.</summary>
public interface IMerchantWithdrawalCap
{
    Task<MerchantWithdrawalCap> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default);
}
