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
public interface IMerchantFeeSchedule
{
    /// <summary>The platform fee taken from a confirmed deposit of <paramref name="receivedAmount"/> base units.</summary>
    Task<BigInteger> QuoteDepositFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default);

    /// <summary>The platform fee charged on a withdrawal of <paramref name="amount"/> base units.</summary>
    Task<BigInteger> QuoteWithdrawalFeeAsync(
        Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Payer-pays-on-top: the gross the payer must send so the merchant nets <paramref name="netTarget"/>
    /// base units after the deposit fee. Used to set a deposit invoice's expected amount.
    /// </summary>
    Task<Result<BigInteger>> GrossUpDepositAsync(
        Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default);
}
