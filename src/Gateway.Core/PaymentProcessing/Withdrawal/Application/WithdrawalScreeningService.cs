using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// Resolves payouts parked in <see cref="WithdrawalStatus.PendingScreening"/>: screens the destination
/// address and routes the payout on the verdict.
///
/// <para><b>Why this is a worker pass and not a step in the request path.</b> The AML provider is rate
/// limited to roughly one call per second, so screening a burst of payouts inline would serialise into the
/// API request and produce timeouts — and a timed-out screening is a payout with no verdict at all, which is
/// the one outcome worth avoiding. Draining a queue at the provider's pace turns that into latency before
/// sending, which the payout pipeline already has.</para>
///
/// <para><b>The reserve is held throughout.</b> A payout reaches this state only after the ledger reserve
/// succeeded, so a Block releases it explicitly (via the aggregate's rejection path) and a hold leaves it in
/// place. Screening never posts a journal itself (§14).</para>
///
/// <para>Payouts are processed one at a time rather than concurrently, so the provider adapter's own pacing
/// is sufficient and no second limiter is needed here.</para>
/// </summary>
public sealed class WithdrawalScreeningService(
    IWithdrawalRepository repository,
    IAddressScreeningService screening,
    IWithdrawalPolicyProvider policies,
    IMerchantApprovalThreshold merchantApprovalThreshold,
    IOptions<WithdrawalScreeningOptions> options,
    TimeProvider timeProvider,
    ILogger<WithdrawalScreeningService> logger)
{
    private readonly WithdrawalScreeningOptions _options = options.Value;

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetByStatusesAsync(
            [WithdrawalStatus.PendingScreening], cancellationToken);

        foreach (var withdrawal in pending)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            await ResolveAsync(withdrawal, cancellationToken);
        }
    }

    private async Task ResolveAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken)
    {
        var verdict = await screening.ScreenAsync(
            withdrawal.Chain, withdrawal.DestinationAddress,
            ScreeningPurpose.PayoutDestination, cancellationToken);

        var now = timeProvider.GetUtcNow();

        // An inconclusive verdict is resolved by OUR policy, not by pretending the provider answered. The
        // default sends it to a human; configuring Allow trades an unscreened payout for continuity during an
        // outage, which is a deliberate operator choice and never implicit.
        var decision = verdict.Decision == ScreeningDecision.Unavailable
                       && _options.OnUnavailable == ScreeningUnavailableBehaviour.Allow
            ? ScreeningDecision.Allow
            : verdict.Decision;

        var result = decision switch
        {
            ScreeningDecision.Block => withdrawal.BlockScreening(
                verdict.ScreeningId, verdict.Score, Describe(verdict), now),

            ScreeningDecision.Review or ScreeningDecision.Unavailable => withdrawal.HoldScreeningForReview(
                verdict.ScreeningId, verdict.Decision.ToString(), verdict.Score, Describe(verdict), now),

            _ => await ClearAsync(withdrawal, verdict, now, cancellationToken)
        };

        if (result.IsFailure)
        {
            // The only way here is a concurrent transition out of PendingScreening. Leave it alone — whoever
            // moved it owns it now — and let the next pass pick up whatever is still pending.
            logger.LogWarning(
                "Screening outcome for withdrawal {WithdrawalId} could not be applied: {Error}",
                withdrawal.Id, result.Error!.Code);
            return;
        }

        await repository.SaveChangesAsync(cancellationToken);

        if (decision is ScreeningDecision.Block or ScreeningDecision.Review or ScreeningDecision.Unavailable)
        {
            logger.LogWarning(
                "Payout {WithdrawalId} screened {Decision} (score {Score}) → {Status}.",
                withdrawal.Id, verdict.Decision, verdict.Score, withdrawal.Status);
        }
    }

    private async Task<SharedKernel.Result> ClearAsync(
        WithdrawalEntity withdrawal, ScreeningVerdict verdict, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Re-resolved here rather than carried from request time, matching the merchant-approval path: the
        // threshold that matters is the one in force when the payout is actually cleared to move.
        var merchantThreshold = await merchantApprovalThreshold.GetAsync(
            withdrawal.MerchantId, withdrawal.AssetId, cancellationToken);
        var threshold = merchantThreshold ?? policies.For(withdrawal.Chain).ApprovalThreshold;

        return withdrawal.ClearScreening(
            verdict.ScreeningId, verdict.Score, withdrawal.Amount > threshold, now);
    }

    /// <summary>A short operator-facing reason. The full evidence, including the provider's report link, is on
    /// the screening record that <c>ScreeningId</c> points at — this is the line shown on the queue.</summary>
    private static string Describe(ScreeningVerdict verdict)
    {
        if (verdict.Decision == ScreeningDecision.Unavailable)
            return "Address screening returned no verdict (provider unavailable).";

        var reasons = verdict.Reasons.Count > 0 ? string.Join(", ", verdict.Reasons) : "no indicators listed";
        return $"Address screening: {verdict.RiskLevel} (score {verdict.Score}) — {reasons}.";
    }
}
