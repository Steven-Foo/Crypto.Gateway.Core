using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// The Merchant module's pricing read model. Deposit (PaymentIntent) and money-out (Withdrawal), and the
/// Ledger's deposit split, resolve fees through this and nothing else — never through the Merchant
/// aggregate or its tables (§4.5). Amounts are unsigned integer base units (§14).
///
/// An unpriced merchant (no asset policy) is quoted a <b>zero</b> fee: a documented operational gap, never
/// an overcharge. The fee arithmetic itself lives in the domain <c>FeeSchedule</c>; this port only resolves
/// the right schedule and delegates.
/// </summary>
/// <summary>
/// A resolved fee quote, with enough detail for the caller to snapshot on the transaction it prices — the
/// merchant-facing "proof" of the calculation (最低手续费 transparency): the final <paramref name="Fee"/>, the
/// exact rate inputs that produced it (<paramref name="Bps"/>/<paramref name="Fixed"/>/<paramref name="Minimum"/>,
/// captured at THIS moment so a later change to the merchant's rate never rewrites history), and whether the
/// minimum-fee floor actually kicked in.
/// </summary>
public sealed record FeeQuote(BigInteger Fee, int Bps, BigInteger Fixed, BigInteger Minimum, bool MinimumApplied);

public interface IMerchantFeeSchedule
{
    /// <summary>The platform fee taken from a confirmed deposit of <paramref name="receivedAmount"/> base units.</summary>
    Task<FeeQuote> QuoteDepositFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default);

    /// <summary>
    /// The platform fee taken from a <b>merchant top-up</b> of <paramref name="receivedAmount"/> base units —
    /// the merchant funding its own balance, priced separately from a customer's deposit.
    ///
    /// <para>Zero unless an admin has explicitly declared a top-up rate for this merchant: unlike the deposit
    /// and withdrawal quotes, this deliberately does <em>not</em> fall back to the platform default fee, so a
    /// merchant is never silently charged to fund its own float. There is also no gross-up counterpart — a
    /// top-up fee is deducted from what arrives, because there is no third-party payer to carry it.</para>
    /// </summary>
    Task<BigInteger> QuoteTopUpFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default);

    /// <summary>The platform fee charged on a withdrawal of <paramref name="amount"/> base units.</summary>
    Task<FeeQuote> QuoteWithdrawalFeeAsync(
        Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default);

}
