using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;

/// <summary>
/// The public read model behind a hosted pay page. Carries only what the payer needs — no internal id, no
/// merchant data. <see cref="ExpectedAmountBaseUnits"/> is an exact base-unit integer string; the host
/// converts it to a display value at the edge (§14). <see cref="Status"/> is the effective status
/// ("pending" | "confirmed" | "expired"), already accounting for a lapsed-but-not-yet-swept expiry.
/// </summary>
public sealed record PaymentIntentView(
    Guid PublicReference,
    Guid AssetId,
    string Address,
    string ExpectedAmountBaseUnits,
    string Status,
    DateTimeOffset ExpiresAt);

/// <summary>Ops search filters — every field optional and AND-combined. <see cref="ReceivingAddress"/> is the
/// invoice's assigned deposit address; <see cref="AssetId"/> is resolved from a "coin" symbol by the caller
/// (the host owns <c>IAssetCatalog</c>, PaymentIntent does not — §4.5).</summary>
public sealed record PaymentIntentAdminFilter(
    Guid? MerchantId,
    Guid? SystemOrderNumber,
    string? MerchantOrderNumber,
    string? ReceivingAddress,
    Chain? Network,
    Guid? AssetId,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate);

/// <summary>The Ops transaction-search read model for one deposit invoice. <see cref="Status"/> is the
/// effective, already-collapsed vocabulary ("pending" | "confirmed" | "expired" | "failed") — the same one
/// <see cref="PaymentIntentView"/> exposes to the pay page. <see cref="MatchedDepositId"/> lets the caller
/// batch-resolve the matched deposit's amount/confirmations via Deposit's own Contract, without PaymentIntent
/// needing to know Deposit's schema (§4.5).</summary>
public sealed record PaymentIntentAdminRow(
    Guid MerchantId,
    Guid PublicReference,
    string MerchantTransactionId,
    Chain Chain,
    Guid AssetId,
    string Address,
    string ExpectedAmountBaseUnits,
    string Status,
    Guid? MatchedDepositId,
    DateTimeOffset CreatedAt);

public interface IPaymentIntentDirectory
{
    Task<PaymentIntentView?> FindByPublicReferenceAsync(Guid publicReference, CancellationToken cancellationToken = default);

    /// <summary>Looks up an invoice by the merchant's own transaction reference — the merchant-facing
    /// transaction-query endpoint's deposit-side lookup. Scoped to <paramref name="merchantId"/>: this
    /// reference is only unique per-merchant, never globally.</summary>
    Task<PaymentIntentView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a merchant's own transaction reference to the confirmed deposit it matched, if any — the
    /// bridge an ops transaction search needs to go from "the string a merchant gave us" to the Ledger's
    /// <c>ReferenceId</c>, without the Ledger ever needing to know PaymentIntent exists (§4.5). Null if no
    /// intent exists for that reference, or it exists but hasn't matched a deposit yet.
    /// </summary>
    Task<Guid?> FindMatchedDepositIdAsync(Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default);

    /// <summary>Paged, filtered search behind the Ops deposit-transactions screen — newest first.</summary>
    Task<(IReadOnlyList<PaymentIntentAdminRow> Items, int TotalCount)> SearchAsync(
        PaymentIntentAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}
