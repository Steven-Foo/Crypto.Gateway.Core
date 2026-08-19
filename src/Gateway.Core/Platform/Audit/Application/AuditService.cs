using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

public sealed record LogAuditEntryCommand(
    Guid StaffUserId, string StaffUsername, string Action, string EntityType, string? EntityId,
    string? Reason, string? IpAddress);

public sealed record AuditEntryView(
    Guid Id, Guid StaffUserId, string StaffUsername, string Action, string EntityType, string? EntityId,
    string? Reason, string? IpAddress, DateTimeOffset CreatedAt);

/// <summary>Ops search filters — every field optional and AND-combined, same convention as
/// <c>WithdrawalAdminFilter</c>/<c>PaymentIntentAdminFilter</c>.</summary>
public sealed record AuditSearchFilter(
    Guid? StaffUserId, string? Action, string? EntityType, string? EntityId, DateTimeOffset? FromDate, DateTimeOffset? ToDate);

/// <summary>
/// The write port every Ops mutating endpoint calls after a successful action (§4.5 — the host, not the
/// business modules, bridges into Audit, so Merchant/Withdrawal/Identity/etc. never need to know Audit
/// exists). Never fails on a business rule — logging has none; an infrastructure failure throws naturally.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(LogAuditEntryCommand command, CancellationToken cancellationToken = default);
}

/// <summary>The read side behind the Ops audit-search screen.</summary>
public interface IAuditQuery
{
    Task<(IReadOnlyList<AuditEntryView> Items, int TotalCount)> SearchAsync(
        AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>One class serves both interfaces — same convention as <c>StaffAuthService</c>: they share the
/// same small dependency set and neither is complex enough alone to earn its own file.</summary>
public sealed class AuditService(IAuditEntryRepository repository, TimeProvider timeProvider) : IAuditLogger, IAuditQuery
{
    public async Task LogAsync(LogAuditEntryCommand command, CancellationToken cancellationToken = default)
    {
        var entry = AuditEntry.Record(
            command.StaffUserId, command.StaffUsername, command.Action, command.EntityType, command.EntityId,
            command.Reason, command.IpAddress, timeProvider.GetUtcNow());

        repository.Add(entry);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AuditEntryView> Items, int TotalCount)> SearchAsync(
        AuditSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (items, total) = await repository.SearchAsync(filter, page, pageSize, cancellationToken);
        return (items.Select(ToView).ToList(), total);
    }

    private static AuditEntryView ToView(AuditEntry e) =>
        new(e.Id, e.StaffUserId, e.StaffUsername, e.Action, e.EntityType, e.EntityId, e.Reason, e.IpAddress, e.CreatedAt);
}
