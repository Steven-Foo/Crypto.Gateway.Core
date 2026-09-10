using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;

public interface IAuditEntryRepository
{
    void Add(AuditEntry entry);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary><paramref name="merchantId"/> null means "no tenant narrowing" (platform staff, who see
    /// everything); a value restricts to that tenant's own entries. Resolved from an <c>AuditScope</c> by the
    /// service, never taken straight from a request.</summary>
    Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> SearchAsync(
        AuditSearchFilter filter, Guid? merchantId, int page, int pageSize, CancellationToken cancellationToken = default);
}
