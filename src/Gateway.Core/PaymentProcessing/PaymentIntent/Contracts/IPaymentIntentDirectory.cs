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
    DateTimeOffset? ToDate,
    /// <summary>Narrows to any of these merchants — how a merchant-<em>name</em> search (resolved to ids by the
    /// host via Merchant's <c>IMerchantDirectory</c>, §4.5) is expressed here without PaymentIntent knowing
    /// Merchant's schema. AND-combined with <see cref="MerchantId"/> if both happen to be set.</summary>
    IReadOnlyList<Guid>? MerchantIds = null,
    /// <summary>Narrows to one <em>effective</em> status ("pending" | "confirmed" | "expired" | "failed") —
    /// the collapsed vocabulary the read rows expose, not the domain enum. Note "expired" and "pending" are
    /// time-dependent (a lapsed-but-not-yet-swept invoice already reads expired), so the filter evaluates the
    /// same clock the projection does. An unrecognised value matches nothing, never everything.</summary>
    string? Status = null);

/// <summary>
/// The effective-status vocabulary the Ops deposit rows expose and <see cref="PaymentIntentAdminFilter.Status"/>
/// accepts. Published here so a host can reject an unknown value with a 400 rather than hand an operator a
/// silently empty page.
/// </summary>
public static class PaymentIntentEffectiveStatuses
{
    public static readonly string[] All = ["pending", "confirmed", "expired", "failed"];

    public static bool IsKnown(string value) => All.Contains(value.Trim().ToLowerInvariant());
}

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
    DateTimeOffset CreatedAt,
    /// <summary>"Customer" (a payment from the merchant's own customer) or "MerchantTopUp" (the merchant
    /// funding its own balance). Carried as a string so Contracts stay Domain-free. Without this a top-up is
    /// indistinguishable from a customer payment on every deposit list, even though the two are priced on
    /// different fee schedules and settle differently (a top-up is exempt from the T+N hold) — so a merchant
    /// reconciling its books cannot separate "money my customers paid me" from "money I paid in myself".
    /// Defaulted so existing call sites are unaffected.</summary>
    string Kind = "Customer");

/// <summary>Aggregate totals across the ENTIRE filtered set — not the current page — behind the Ops
/// deposit-transactions screen's summary row. <see cref="TotalExpectedAmountBaseUnits"/> sums every matching
/// invoice's requested amount (an exact base-unit integer string, §14). <see cref="MatchedDepositIds"/> is
/// every non-null <c>MatchedDepositId</c> in the filtered set, for the caller to further sum the actual
/// received amount/fee via Deposit's own <c>IDepositLookup</c> (§4.5 — PaymentIntent doesn't know Deposit's
/// Amount/Fee schema). <see cref="DistinctAssetCount"/> is how many different assets appear in the filtered
/// set — summing amounts across different-decimal assets into one number is meaningless, so a caller
/// combining these into a single display total should treat it as approximate/flag it when this is &gt; 1.</summary>
public sealed record PaymentIntentTotals(
    string TotalExpectedAmountBaseUnits,
    int DistinctAssetCount,
    IReadOnlyList<Guid> MatchedDepositIds);

/// <summary>
/// Answers one question for the deposit scanner: "is the invoice currently holding this address a customer
/// payment or a merchant top-up?" — deliberately its own tiny port rather than a method on
/// <see cref="IPaymentIntentDirectory"/>, because the scanner is on the money path and should depend on the
/// single fact it needs, not on the whole admin/reporting read model (§4.5).
///
/// <para>The answer is unambiguous by construction: <c>UX_PaymentIntent_LiveWallet</c> allows at most one
/// <c>Waiting</c> invoice per address, so there is never a choice to arbitrate. An address with no waiting
/// invoice (a direct or late transfer) returns null and is treated as a customer deposit.</para>
/// </summary>
public interface IDepositKindResolver
{
    /// <summary>"Customer" or "MerchantTopUp" for the invoice holding <paramref name="walletId"/>; null if
    /// no invoice is waiting on it. A string, so Deposit never references PaymentIntent's domain type.</summary>
    Task<string?> FindWaitingKindAsync(Guid walletId, CancellationToken cancellationToken = default);
}

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

    /// <summary>Same filter as <see cref="SearchAsync"/>, but aggregated over the whole matching set instead
    /// of one page — the Ops screen's summary totals.</summary>
    Task<PaymentIntentTotals> GetTotalsAsync(PaymentIntentAdminFilter filter, CancellationToken cancellationToken = default);
}
