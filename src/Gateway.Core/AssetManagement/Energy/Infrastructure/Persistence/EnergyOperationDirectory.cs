using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

/// <summary>
/// The read side of the energy-operation state machine (§4.5) — a no-tracking query for the back-office. It
/// never mutates and holds no keys, so a host can compose it without the signer/broadcaster the workers need
/// (§4.7). Entities are materialised then mapped in memory (a <see cref="System.Numerics.BigInteger"/> amount
/// isn't SQL-projectable), mirroring <c>WithdrawalDirectory</c>.
/// </summary>
public sealed class EnergyOperationDirectory(EnergyDbContext context) : IEnergyOperationDirectory
{
    public async Task<(IReadOnlyList<EnergyOperationAdminRow> Items, int TotalCount)> SearchAsync(
        EnergyOperationAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.EnergyOperations.AsNoTracking()
            .Where(o => filter.Chain == null || o.Chain == filter.Chain)
            .Where(o => filter.StakingWalletId == null || o.StakingWalletId == filter.StakingWalletId)
            .Where(o => filter.FromDate == null || o.CreatedAt >= filter.FromDate)
            .Where(o => filter.ToDate == null || o.CreatedAt <= filter.ToDate);

        // Optional kind/status filters. An unrecognised value is ignored (no narrowing) — the host pre-validates.
        if (!string.IsNullOrWhiteSpace(filter.Kind)
            && Enum.TryParse<EnergyOperationKind>(filter.Kind, ignoreCase: true, out var kind))
            query = query.Where(o => o.Kind == kind);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<EnergyOperationStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(o => o.Status == status);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.Select(ToAdminRow).ToList(), totalCount);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default)
    {
        var counts = await context.EnergyOperations.AsNoTracking()
            .Where(o => chain == null || o.Chain == chain)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => c.Status.ToString(), c => c.Count);
    }

    private static EnergyOperationAdminRow ToAdminRow(EnergyOperation operation) => new(
        operation.Id,
        operation.Kind.ToString(),
        operation.Chain.ToString(),
        operation.StakingWalletId,
        operation.OwnerAddress,
        operation.TargetAddress,
        operation.AmountSun.ToString(CultureInfo.InvariantCulture),
        operation.Status.ToString(),
        operation.TransactionHash,
        operation.Confirmations,
        operation.FailureReason,
        operation.CreatedAt,
        operation.UpdatedAt);
}
