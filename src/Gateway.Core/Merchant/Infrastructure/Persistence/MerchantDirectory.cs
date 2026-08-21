using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>
/// Read-only projection for other modules. Never exposes credentials, and never returns the
/// aggregate itself — a consumer must not be able to mutate a merchant through this.
/// </summary>
public sealed class MerchantDirectory(MerchantDbContext context) : IMerchantDirectory
{
    public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        Project(context.Merchants.AsNoTracking().Where(m => m.Id == merchantId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default)
    {
        var normalised = merchantCode.Trim().ToUpperInvariant();
        return Project(context.Merchants.AsNoTracking().Where(m => m.MerchantCode == normalised))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyList<Guid> merchantIds, CancellationToken cancellationToken = default)
    {
        if (merchantIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await context.Merchants.AsNoTracking()
            .Where(m => merchantIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name })
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string nameContains, CancellationToken cancellationToken = default)
    {
        var term = nameContains.Trim();
        return await context.Merchants.AsNoTracking()
            .Where(m => EF.Functions.Like(m.Name, $"%{term}%") || EF.Functions.Like(m.MerchantCode, $"%{term}%"))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<MerchantSummary> Project(IQueryable<Domain.Merchant> query) =>
        query.Select(m => new MerchantSummary(
            m.Id,
            m.MerchantCode,
            m.Name,
            m.CallbackUrl,
            m.Status == MerchantStatus.Active,
            m.SettlementDelayDays));
}
