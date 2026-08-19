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
    string? Kind = null);

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
}
