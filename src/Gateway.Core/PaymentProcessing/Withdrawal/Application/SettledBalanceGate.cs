using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// Resolves a merchant's <b>settled</b> (withdrawable) balance for an asset from its settlement period (T+N).
/// A deposit confirmed on calendar day D (UTC) matures at 00:00 UTC of day D+N; only matured funds may leave —
/// this gate applies to BOTH user payouts and the merchant's own earnings cash-out. T+0 short-circuits to the
/// full available balance (no maturation), avoiding the extra journal aggregate on the common path.
/// </summary>
public sealed class SettledBalanceGate(ILedgerQuery ledgerQuery, TimeProvider timeProvider)
{
    public Task<BigInteger> GetSettledAvailableAsync(
        Guid merchantId, Guid assetId, int settlementDelayDays, CancellationToken cancellationToken = default)
    {
        if (settlementDelayDays <= 0)
            return ledgerQuery.GetMerchantBalanceAsync(merchantId, assetId, cancellationToken);

        // Unmatured iff the deposit's journal is dated on/after this cutoff. A deposit on day D matures at
        // 00:00 UTC of D+N ⇒ at 'now' on day T it is matured iff D ≤ T−N ⇒ unmatured iff D ≥ T−N+1, i.e. the
        // journal date ≥ start-of-today-UTC minus (N−1) days.
        var startOfTodayUtc = new DateTimeOffset(timeProvider.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var cutoffUtc = startOfTodayUtc.AddDays(1 - settlementDelayDays);
        return ledgerQuery.GetMerchantSettledBalanceAsync(merchantId, assetId, cutoffUtc, cancellationToken);
    }
}
