using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure.Persistence;

public sealed class AuditEntryRepository(AuditDbContext context) : IAuditEntryRepository
{
    public void Add(AuditEntry entry) => context.AuditEntries.Add(entry);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    public async Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> SearchAsync(
        AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.AuditEntries.AsNoTracking().AsQueryable();

        if (filter.StaffUserId is { } staffUserId)
            query = query.Where(e => e.StaffUserId == staffUserId);
        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(e => e.Action == filter.Action);
        if (!string.IsNullOrWhiteSpace(filter.EntityType))
            query = query.Where(e => e.EntityType == filter.EntityType);
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            query = query.Where(e => e.EntityId == filter.EntityId);
        if (filter.FromDate is { } from)
            query = query.Where(e => e.CreatedAt >= from);
        if (filter.ToDate is { } to)
            query = query.Where(e => e.CreatedAt <= to);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
