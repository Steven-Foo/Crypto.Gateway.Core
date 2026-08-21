using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;

/// <summary>
/// A read-only view over the sweep state machine for the back-office (§4.5 — the one seam a host may consume,
/// never the aggregate or repository). Amounts are exact base-unit strings (§14): the host converts to a
/// display value at the edge, and keeps the precise integer for audit. <see cref="Status"/> is the domain
/// status name as a string, so this Contract stays Domain-free.
/// </summary>
public sealed record SweepAdminRow(
    Guid SweepId,
    Guid WalletId,
    string Chain,
    Guid AssetId,
    string FromAddress,
    string ToAddress,
    string AmountBaseUnits,
    string Status,
    string? TransactionHash,
    int? Confirmations,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Optional narrowing for a sweep search. All fields are AND-combined; a null field is not applied.
/// <see cref="Status"/> is a domain status name (case-insensitive); an unrecognised value is simply ignored
/// (no narrowing) — the host validates it upstream.</summary>
public sealed record SweepAdminFilter(
    Chain? Chain = null,
    string? Status = null,
    Guid? WalletId = null,
    Guid? AssetId = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null);

public interface ISweepDirectory
{
    /// <summary>A newest-first page of sweeps matching the filter, plus the unpaged total count.</summary>
    Task<(IReadOnlyList<SweepAdminRow> Items, int TotalCount)> SearchAsync(
        SweepAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Count of sweeps by status (optionally scoped to one chain) — the summary a dashboard shows
    /// above the list. Keyed by the domain status name.</summary>
    Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default);
}
