using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;

public interface IAuditEntryRepository
{
    void Add(AuditEntry entry);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> SearchAsync(
        AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}
