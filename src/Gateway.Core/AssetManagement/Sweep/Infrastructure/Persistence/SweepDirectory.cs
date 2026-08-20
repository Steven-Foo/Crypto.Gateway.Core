using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using SweepEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain.Sweep;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

/// <summary>
/// The read side of Sweep (§4.5) — a no-tracking query over the sweep table for the back-office. It never
/// mutates and holds no keys, so a host can compose it without the signer/broadcaster the workers need
/// (§4.7). Entities are materialised then mapped in memory (a <see cref="System.Numerics.BigInteger"/> amount
/// isn't SQL-projectable), mirroring <c>WithdrawalDirectory</c>.
/// </summary>
public sealed class SweepDirectory(SweepDbContext context) : ISweepDirectory
{
    public async Task<(IReadOnlyList<SweepAdminRow> Items, int TotalCount)> SearchAsync(
        SweepAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.Sweeps.AsNoTracking()
            .Where(s => filter.Chain == null || s.Chain == filter.Chain)
            .Where(s => filter.WalletId == null || s.WalletId == filter.WalletId)
            .Where(s => filter.AssetId == null || s.AssetId == filter.AssetId)
            .Where(s => filter.FromDate == null || s.CreatedAt >= filter.FromDate)
            .Where(s => filter.ToDate == null || s.CreatedAt <= filter.ToDate);

        // Optional status filter. An unrecognised value is ignored (no narrowing) — the host pre-validates it.
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<SweepStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(s => s.Status == status);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.Select(ToAdminRow).ToList(), totalCount);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default)
    {
        var counts = await context.Sweeps.AsNoTracking()
            .Where(s => chain == null || s.Chain == chain)
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => c.Status.ToString(), c => c.Count);
    }

    private static SweepAdminRow ToAdminRow(SweepEntity sweep) => new(
        sweep.Id,
        sweep.WalletId,
        sweep.Chain.ToString(),
        sweep.AssetId,
        sweep.FromAddress,
        sweep.ToAddress,
        sweep.Amount.ToString(CultureInfo.InvariantCulture),
        sweep.Status.ToString(),
        sweep.TransactionHash,
        sweep.Confirmations,
        sweep.FailureReason,
        sweep.CreatedAt,
        sweep.UpdatedAt);
}
