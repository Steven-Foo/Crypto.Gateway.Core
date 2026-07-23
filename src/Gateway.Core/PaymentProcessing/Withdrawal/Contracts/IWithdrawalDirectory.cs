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

public interface IWithdrawalDirectory
{
    /// <summary>Looks up a withdrawal by the merchant's own idempotency key — the merchant-facing
    /// transaction-query endpoint's withdrawal-side lookup. Scoped to <paramref name="merchantId"/>: this
    /// key is only unique per-merchant, never globally.</summary>
    Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default);
}
