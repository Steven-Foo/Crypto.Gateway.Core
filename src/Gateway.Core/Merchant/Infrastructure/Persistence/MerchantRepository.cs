using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantRepository(MerchantDbContext context) : IMerchantRepository
{
    public Task<Domain.Merchant?> GetByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        context.Merchants
            .Include(m => m.Configuration)
            .Include(m => m.Credentials)
            .Include(m => m.AssetPolicies)
            .SingleOrDefaultAsync(m => m.Id == merchantId, cancellationToken);

    public Task<Domain.Merchant?> GetByCodeAsync(string merchantCode, CancellationToken cancellationToken = default)
    {
        var normalised = merchantCode.Trim().ToUpperInvariant();
        return context.Merchants
            .Include(m => m.Configuration)
            .Include(m => m.Credentials)
            .Include(m => m.AssetPolicies)
            .SingleOrDefaultAsync(m => m.MerchantCode == normalised, cancellationToken);
    }

    public Task<bool> CodeExistsAsync(string merchantCode, CancellationToken cancellationToken = default)
    {
        var normalised = merchantCode.Trim().ToUpperInvariant();
        return context.Merchants.AnyAsync(m => m.MerchantCode == normalised, cancellationToken);
    }

    public Task<MerchantApiCredential?> FindActiveCredentialAsync(
        string apiKey,
        CancellationToken cancellationToken = default) =>
        context.Credentials
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.ApiKey == apiKey && c.Status == CredentialStatus.Active, cancellationToken);

    public Task<MerchantApiCredential?> FindActiveCredentialByMerchantAsync(
        Guid merchantId,
        CancellationToken cancellationToken = default) =>
        context.Credentials
            .AsNoTracking()
            .Where(c => c.MerchantId == merchantId && c.Status == CredentialStatus.Active)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(Domain.Merchant merchant) => context.Merchants.Add(merchant);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
