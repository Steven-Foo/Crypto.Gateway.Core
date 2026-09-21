using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

public sealed class AddressScreeningRepository(ComplianceDbContext db) : IAddressScreeningRepository
{
    public async Task<AddressScreening?> FindLatestAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        // No tracking: this read decides whether to spend a provider call, and never mutates.
        // The comparison relies on the column's case-insensitive collation, so a caller supplying a
        // differently-cased EVM address still finds the same history rather than silently re-screening.
        return await db.AddressScreenings
            .AsNoTracking()
            .Where(s => s.Chain == chain && s.Address == address)
            .OrderByDescending(s => s.ScreenedAt)
            // Tie-break on Seq, the insertion order, exactly as AddressScreeningDirectory does. Without it two
            // rows sharing a timestamp could resolve to one verdict here and another on the ops screens —
            // and this read is the one the payout gate acts on.
            .ThenByDescending(s => EF.Property<long>(s, AddressScreeningDirectory.SeqProperty))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddAsync(AddressScreening screening, CancellationToken cancellationToken = default)
    {
        db.AddressScreenings.Add(screening);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FindFreshlyScreenedAsync(
        Chain chain,
        IReadOnlyCollection<string> addresses,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        // Rides IX_AddressScreening_Chain_Address_ScreenedAt. Returns DISTINCT addresses rather than rows:
        // an address screened many times has many rows, and only the question "is any of them still fresh"
        // matters here. A failed screening has a null FreshUntil and is therefore never counted as fresh,
        // which is what stops one outage pinning an address to "unknown" for a whole cache window.
        return await db.AddressScreenings
            .AsNoTracking()
            .Where(s => s.Chain == chain && addresses.Contains(s.Address) && s.FreshUntil > now)
            .Select(s => s.Address)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
