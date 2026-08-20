using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;

/// <summary>
/// A read-only view over one energy operation (stake / delegate / native-TRX top-up) for the back-office
/// (§4.5). Amounts are exact base-unit strings — TRX in sun (§14); the host converts to a display value at the
/// edge and keeps the precise integer. <see cref="Kind"/> and <see cref="Status"/> are the domain names as
/// strings, so this Contract stays Domain-free.
/// </summary>
public sealed record EnergyOperationAdminRow(
    Guid OperationId,
    string Kind,
    string Chain,
    Guid StakingWalletId,
    string OwnerAddress,
    string? TargetAddress,
    string AmountSunBaseUnits,
    string Status,
    string? TransactionHash,
    int? Confirmations,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Optional narrowing for an energy-operation search. All fields AND-combine; a null field is not
/// applied. <see cref="Kind"/>/<see cref="Status"/> are domain names (case-insensitive); an unrecognised value
/// is ignored (no narrowing) — the host validates upstream.</summary>
public sealed record EnergyOperationAdminFilter(
    Chain? Chain = null,
    string? Kind = null,
    string? Status = null,
    Guid? StakingWalletId = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null);

public interface IEnergyOperationDirectory
{
    /// <summary>A newest-first page of energy operations matching the filter, plus the unpaged total count.</summary>
    Task<(IReadOnlyList<EnergyOperationAdminRow> Items, int TotalCount)> SearchAsync(
        EnergyOperationAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Count of operations by status (optionally scoped to one chain) — the dashboard summary.
    /// Keyed by the domain status name.</summary>
    Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default);
}
