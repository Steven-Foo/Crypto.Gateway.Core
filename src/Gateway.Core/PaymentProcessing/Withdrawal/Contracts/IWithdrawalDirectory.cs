using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;

/// <summary>The public read model behind the merchant transaction-query endpoint's withdrawal-side lookup.
/// <see cref="AmountBaseUnits"/>/<see cref="FeeBaseUnits"/> are exact base-unit integer strings; the host
/// converts to display values at the edge (§14). <see cref="Status"/> is the raw lifecycle name
/// (<c>Withdrawal.Domain.WithdrawalStatus</c>) — the host maps it onto whatever vocabulary it exposes.</summary>
public sealed record WithdrawalView(
    Guid WithdrawalId,
    Guid AssetId,
    Chain Chain,
    string DestinationAddress,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string Status,
    string? TransactionHash,
    DateTimeOffset CreatedAt);

/// <summary>Ops search filters — every field optional and AND-combined. <see cref="AssetId"/> is resolved
/// from a "coin" symbol by the caller (the host owns <c>IAssetCatalog</c>, Withdrawal does not — §4.5).
/// <see cref="Kind"/> is the withdrawal-kind name ("User" | "Merchant"); an unrecognised value is ignored.</summary>
public sealed record WithdrawalAdminFilter(
    Guid? MerchantId,
    Guid? SystemOrderNumber,
    string? MerchantOrderNumber,
    string? ReceivingAddress,
    Chain? Network,
    Guid? AssetId,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    string? Kind = null,
    /// <summary>Narrows to any of these merchants — how a merchant-<em>name</em> search (resolved to ids by the
    /// host via Merchant's <c>IMerchantDirectory</c>, §4.5) is expressed here without Withdrawal knowing
    /// Merchant's schema. AND-combined with <see cref="MerchantId"/> if both happen to be set.</summary>
    IReadOnlyList<Guid>? MerchantIds = null);

/// <summary>The Ops transaction-search read model for one withdrawal. <see cref="Status"/> is the effective,
/// already-collapsed vocabulary ("pending" | "pending_approval" | "insufficient_balance" | "awaiting_release" |
/// "confirmed" | "failed"). <see cref="StatusReason"/> carries the parked-hold detail ("needs X, has Y") so ops
/// can trace a stalled payout without opening the record; null unless the withdrawal is on a funding hold.
/// <see cref="TransactionHash"/> is null until broadcast. <see cref="SourceWalletId"/> is the hot-pool wallet
/// leased at signing (see <c>Withdrawal.SourceWalletId</c>) — null until the withdrawal reaches Signing —
/// lets ops cross-reference a stalled/insufficient-balance payout to the exact pool wallet it's waiting on.</summary>
public sealed record WithdrawalAdminRow(
    Guid MerchantId,
    Guid WithdrawalId,
    string MerchantTransactionId,
    Chain Chain,
    Guid AssetId,
    string DestinationAddress,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string Status,
    string? StatusReason,
    int? Confirmations,
    string? TransactionHash,
    Guid? SourceWalletId,
    DateTimeOffset CreatedAt,
    string Kind);

/// <summary>Aggregate totals across the ENTIRE filtered set — not the current page — behind the Ops
/// withdrawal-transactions screen's summary row. Both sums are exact base-unit integer strings (§14).
/// <see cref="DistinctAssetCount"/> is how many different assets appear in the filtered set — summing
/// amounts across different-decimal assets into one number is meaningless, so a caller combining these into
/// a single display total should treat it as approximate/flag it when this is &gt; 1.</summary>
public sealed record WithdrawalTotals(string TotalAmountBaseUnits, string TotalFeeBaseUnits, int DistinctAssetCount);

public interface IWithdrawalDirectory
{
    /// <summary>Looks up a withdrawal by the merchant's own transaction id, scoped to
    /// <paramref name="merchantId"/> and a specific <paramref name="kind"/> ("User" | "Merchant"). The id is
    /// unique only per <c>(merchant, kind, reference)</c>, so a user payout and a merchant cash-out can reuse
    /// the same reference — the kind disambiguates which one to return. An unrecognised kind falls back to
    /// "User".</summary>
    Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, string kind = "User", CancellationToken cancellationToken = default);

    /// <summary>Paged, filtered search behind the Ops withdrawal-transactions screen — newest first.</summary>
    Task<(IReadOnlyList<WithdrawalAdminRow> Items, int TotalCount)> SearchAsync(
        WithdrawalAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Same filter as <see cref="SearchAsync"/>, but aggregated over the whole matching set instead
    /// of one page — the Ops screen's summary totals.</summary>
    Task<WithdrawalTotals> GetTotalsAsync(WithdrawalAdminFilter filter, CancellationToken cancellationToken = default);
}
