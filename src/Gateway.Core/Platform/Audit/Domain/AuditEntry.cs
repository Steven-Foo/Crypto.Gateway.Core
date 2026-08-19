using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Domain;

/// <summary>
/// One recorded staff action in the Back Office — who did what, to what, and when. Deliberately a plain
/// record, not a state machine: an audit entry has no invariant a trusted internal caller could violate
/// (unlike money-moving aggregates), so there is no <c>Result</c>-wrapped factory here. v1 scope is
/// "who/what/when/outcome," not a field-level before/after diff — see the module's own notes for why.
/// </summary>
public sealed class AuditEntry : Entity<Guid>
{
    private AuditEntry(
        Guid id, Guid staffUserId, string staffUsername, string action, string entityType, string? entityId,
        string? reason, string? ipAddress, DateTimeOffset createdAt) : base(id)
    {
        StaffUserId = staffUserId;
        StaffUsername = staffUsername;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Reason = reason;
        IpAddress = ipAddress;
        CreatedAt = createdAt;
    }

    private AuditEntry() : base(Guid.Empty)
    {
    }

    public Guid StaffUserId { get; private set; }

    /// <summary>Snapshotted at write time (from the acting session), not a live lookup — history reads
    /// correctly even if the account is later renamed or disabled.</summary>
    public string StaffUsername { get; private set; } = null!;

    /// <summary>A stable code, e.g. <c>"withdrawal.approved"</c>, <c>"merchant.fee_updated"</c> — the host
    /// endpoint that performed the action owns this vocabulary (§4.5: Audit doesn't know what the codes mean).</summary>
    public string Action { get; private set; } = null!;

    public string EntityType { get; private set; } = null!;
    public string? EntityId { get; private set; }
    public string? Reason { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditEntry Record(
        Guid staffUserId, string staffUsername, string action, string entityType, string? entityId,
        string? reason, string? ipAddress, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), staffUserId, staffUsername, action, entityType, entityId, reason, ipAddress, now);
}
