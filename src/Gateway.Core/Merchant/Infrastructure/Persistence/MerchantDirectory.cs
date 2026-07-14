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

    private static IQueryable<MerchantSummary> Project(IQueryable<Domain.Merchant> query) =>
        query.Select(m => new MerchantSummary(
            m.Id,
            m.MerchantCode,
            m.Name,
            m.CallbackUrl,
            m.Status == MerchantStatus.Active));
}
