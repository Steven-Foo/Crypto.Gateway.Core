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
        string? reason, string? ipAddress, DateTimeOffset createdAt, Guid? merchantId) : base(id)
    {
        StaffUserId = staffUserId;
        StaffUsername = staffUsername;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Reason = reason;
        IpAddress = ipAddress;
        CreatedAt = createdAt;
        MerchantId = merchantId;
    }

    private AuditEntry() : base(Guid.Empty)
    {
    }

    /// <summary>
    /// The tenant whose portal the action was performed in, or <c>null</c> for a platform-staff action in the
    /// Back Office. This is what separates the two audiences sharing this table: a merchant may only ever read
    /// entries carrying its OWN id, so a staff entry (null) can never leak into a merchant's log, and one
    /// merchant can never see another's. Enforced in <c>AuditService.SearchAsync</c>, which requires an
    /// explicit actor scope rather than trusting a caller-supplied filter.
    /// </summary>
    public Guid? MerchantId { get; private set; }

    /// <summary>The acting user's id — a staff user when <see cref="MerchantId"/> is null, otherwise the
    /// merchant-portal user. Kept as one column because "who acted" means the same thing to both audiences and
    /// splitting it would make every read a two-column coalesce for no gain.</summary>
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

    /// <summary>Records a platform-staff action (no tenant). <paramref name="merchantId"/> is defaulted so
    /// every existing Ops call site is unchanged; the merchant portal passes its session's tenant.</summary>
    public static AuditEntry Record(
        Guid staffUserId, string staffUsername, string action, string entityType, string? entityId,
        string? reason, string? ipAddress, DateTimeOffset now, Guid? merchantId = null) =>
        new(Guid.CreateVersion7(), staffUserId, staffUsername, action, entityType, entityId, reason, ipAddress, now, merchantId);
}
