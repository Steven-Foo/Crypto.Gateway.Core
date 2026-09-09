using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.Data.SqlClient;
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

    /// <summary>"ME" + 5 digits = 7 characters — matches the shape <c>MerchantRegistrar</c> generates.
    /// Filtering to that shape before parsing means a hand-picked dev/legacy code (e.g. "DEMOACME", also
    /// 8 characters) can never be mistaken for a generated one and perturb the sequence.</summary>
    private const int GeneratedCodeLength = 7;

    public async Task<int> GetNextMerchantCodeSequenceAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await context.Merchants
            .Where(m => m.MerchantCode.Length == GeneratedCodeLength && m.MerchantCode.StartsWith("ME"))
            .Select(m => m.MerchantCode)
            .ToListAsync(cancellationToken);

        var maxSequence = 0;
        foreach (var code in candidates)
        {
            if (int.TryParse(code.AsSpan(2), out var sequence) && sequence > maxSequence)
                maxSequence = sequence;
        }

        return maxSequence + 1;
    }

    public async Task<(IReadOnlyList<Domain.Merchant> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.Merchants
            .Include(m => m.Configuration)
            .Include(m => m.Credentials)
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt);

        var total = await context.Merchants.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<string>> GetAllAllowedIpsExceptAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var csvs = await context.Configurations
            .AsNoTracking()
            .Where(c => c.MerchantId != merchantId && c.AllowedIpsCsv != null)
            .Select(c => c.AllowedIpsCsv!)
            .ToListAsync(cancellationToken);

        return [.. csvs
            .SelectMany(csv => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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

    public async Task<bool> TrySaveNewMerchantAsync(Domain.Merchant merchant, CancellationToken cancellationToken = default)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsMerchantCodeUniqueViolation(ex))
        {
            // Lost the insert race for this generated code to a concurrent registration — detach our
            // doomed insert so the caller's retry with a fresh candidate doesn't try to save it again.
            context.Entry(merchant).State = EntityState.Detached;
            return false;
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    private static bool IsMerchantCodeUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("IX_Merchant_MerchantCode", StringComparison.Ordinal);
}
