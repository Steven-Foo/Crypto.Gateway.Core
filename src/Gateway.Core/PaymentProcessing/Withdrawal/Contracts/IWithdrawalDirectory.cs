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
/// from a "coin" symbol by the caller (the host owns <c>IAssetCatalog</c>, Withdrawal does not — §4.5).</summary>
public sealed record WithdrawalAdminFilter(
    Guid? MerchantId,
    Guid? SystemOrderNumber,
    string? MerchantOrderNumber,
    string? ReceivingAddress,
    Chain? Network,
    Guid? AssetId,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate);

/// <summary>The Ops transaction-search read model for one withdrawal. <see cref="Status"/> is the effective,
/// already-collapsed vocabulary ("pending" | "confirmed" | "failed") — withdrawals have no "expired" state.</summary>
public sealed record WithdrawalAdminRow(
    Guid MerchantId,
    Guid WithdrawalId,
    string IdempotencyKey,
    Chain Chain,
    Guid AssetId,
    string DestinationAddress,
    string AmountBaseUnits,
    string Status,
    int? Confirmations,
    DateTimeOffset CreatedAt);

public interface IWithdrawalDirectory
{
    /// <summary>Looks up a withdrawal by the merchant's own idempotency key — the merchant-facing
    /// transaction-query endpoint's withdrawal-side lookup. Scoped to <paramref name="merchantId"/>: this
    /// key is only unique per-merchant, never globally.</summary>
    Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default);

    /// <summary>Paged, filtered search behind the Ops withdrawal-transactions screen — newest first.</summary>
    Task<(IReadOnlyList<WithdrawalAdminRow> Items, int TotalCount)> SearchAsync(
        WithdrawalAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}
