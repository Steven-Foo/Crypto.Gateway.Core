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
            // SettlementWallets is load-bearing, not cosmetic: Merchant.SetSettlementWallet decides
            // update-vs-create by looking for an existing wallet on this chain. Without this Include the
            // collection is always empty, so a REPLACEMENT wallet was treated as a first one and died on the
            // (MerchantId, Chain) unique index — a DbUpdateException, i.e. a 500 rather than a clean result.
            // Staff could therefore set a merchant's cash-out destination once and never change it.
            .Include(m => m.SettlementWallets)
            .SingleOrDefaultAsync(m => m.Id == merchantId, cancellationToken);

    public Task<Domain.Merchant?> GetByCodeAsync(string merchantCode, CancellationToken cancellationToken = default)
    {
        var normalised = merchantCode.Trim().ToUpperInvariant();
        return context.Merchants
            .Include(m => m.Configuration)
            .Include(m => m.Credentials)
            .Include(m => m.AssetPolicies)
            // Same reason as GetByIdAsync above, and it bit here too: this method returns the aggregate for
            // MUTATION, and Merchant.SetSettlementWallet decides update-vs-create from this collection. With
            // it unloaded the dev seeder treated an existing wallet as a first one and failed the
            // (MerchantId, Chain) unique index on every boot of an already-seeded database.
            //
            // The rule, since it has now been missed twice: a read that hands back the aggregate to be
            // changed must load the whole aggregate. Partial loading is safe only for a read-only projection,
            // and those go through MerchantDirectory, not here.
            .Include(m => m.SettlementWallets)
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

    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        // Join an outer transaction if the caller already opened one, so this stays composable.
        if (context.Database.CurrentTransaction is not null)
            return await action(cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await action(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static bool IsMerchantCodeUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("IX_Merchant_MerchantCode", StringComparison.Ordinal);
}
