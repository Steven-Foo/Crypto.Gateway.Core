using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

/// <summary><paramref name="MerchantId"/> is null for a platform-staff action and the acting tenant for a
/// merchant-portal action — see <see cref="Domain.AuditEntry.MerchantId"/>. Defaulted so existing Ops call
/// sites are unchanged.</summary>
public sealed record LogAuditEntryCommand(
    Guid StaffUserId, string StaffUsername, string Action, string EntityType, string? EntityId,
    string? Reason, string? IpAddress, Guid? MerchantId = null);

public sealed record AuditEntryView(
    Guid Id, Guid StaffUserId, string StaffUsername, string Action, string EntityType, string? EntityId,
    string? Reason, string? IpAddress, DateTimeOffset CreatedAt, Guid? MerchantId = null);

/// <summary>Ops search filters — every field optional and AND-combined, same convention as
/// <c>WithdrawalAdminFilter</c>/<c>PaymentIntentAdminFilter</c>. Note this record deliberately carries NO
/// tenant field: who may see what is decided by <see cref="AuditScope"/>, a separate required argument, so a
/// caller cannot read another tenant's history by leaving a filter unset.</summary>
public sealed record AuditSearchFilter(
    Guid? StaffUserId, string? Action, string? EntityType, string? EntityId, DateTimeOffset? FromDate, DateTimeOffset? ToDate);

/// <summary>
/// Who is asking — the audit log's access boundary, kept OUT of <see cref="AuditSearchFilter"/> on purpose.
/// A filter field is something a caller may omit; forgetting it here would silently widen a merchant's read
/// to every tenant's history. As a distinct required parameter the compiler forces each call site to say
/// which it is.
/// </summary>
public abstract record AuditScope
{
    private AuditScope() { }

    /// <summary>Platform staff: sees every entry, including all merchants' portal actions.</summary>
    public sealed record Platform : AuditScope;

    /// <summary>One merchant's portal: sees only entries stamped with this tenant. Staff entries (null
    /// tenant) and other merchants' entries are excluded by the same condition.</summary>
    public sealed record Merchant(Guid MerchantId) : AuditScope;
}

/// <summary>
/// The write port every Ops mutating endpoint calls after a successful action (§4.5 — the host, not the
/// business modules, bridges into Audit, so Merchant/Withdrawal/Identity/etc. never need to know Audit
/// exists). Never fails on a business rule — logging has none; an infrastructure failure throws naturally.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(LogAuditEntryCommand command, CancellationToken cancellationToken = default);
}

/// <summary>The read side behind the Ops audit-search screen and the merchant portal's own activity log.
/// <paramref name="scope"/> is required and decides the audience — see <see cref="AuditScope"/>.</summary>
public interface IAuditQuery
{
    Task<(IReadOnlyList<AuditEntryView> Items, int TotalCount)> SearchAsync(
        AuditScope scope, AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>One class serves both interfaces — same convention as <c>StaffAuthService</c>: they share the
/// same small dependency set and neither is complex enough alone to earn its own file.</summary>
public sealed class AuditService(IAuditEntryRepository repository, TimeProvider timeProvider) : IAuditLogger, IAuditQuery
{
    public async Task LogAsync(LogAuditEntryCommand command, CancellationToken cancellationToken = default)
    {
        var entry = AuditEntry.Record(
            command.StaffUserId, command.StaffUsername, command.Action, command.EntityType, command.EntityId,
            command.Reason, command.IpAddress, timeProvider.GetUtcNow(), command.MerchantId);

        repository.Add(entry);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AuditEntryView> Items, int TotalCount)> SearchAsync(
        AuditScope scope, AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // The tenant narrowing is applied here, not left to the caller's filter: a merchant scope can only
        // ever read rows stamped with its own id.
        var merchantId = scope is AuditScope.Merchant m ? m.MerchantId : (Guid?)null;
        var (items, total) = await repository.SearchAsync(filter, merchantId, page, pageSize, cancellationToken);
        return (items.Select(ToView).ToList(), total);
    }

    private static AuditEntryView ToView(AuditEntry e) =>
        new(e.Id, e.StaffUserId, e.StaffUsername, e.Action, e.EntityType, e.EntityId, e.Reason, e.IpAddress,
            e.CreatedAt, e.MerchantId);
}
