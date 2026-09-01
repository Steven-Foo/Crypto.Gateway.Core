using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

/// <summary>
/// A named set of permission codes owned by ONE merchant — the portal's RBAC unit. Deliberately per-tenant
/// (<see cref="MerchantId"/>): a merchant defines its own roles ("Finance", "Support") and can never see or
/// assign another merchant's, so role management is itself tenant-isolated.
///
/// <para>A permission code is an opaque string owned by the host that enforces it (e.g.
/// <c>portal.payout.create</c>); this module never interprets a code, it only stores and snapshots the set
/// (§4.5). The host validates codes against its own catalog before they ever reach here — so a merchant
/// cannot invent a code, and above all cannot grant itself a platform/ops code, which this module would
/// otherwise happily store.</para>
///
/// <para><see cref="WildcardPermission"/> grants every <em>portal</em> permission (the portal host only ever
/// checks portal codes against it), used for the built-in per-merchant Administrator role so a tenant always
/// has one account that cannot be locked out.</para>
/// </summary>
public sealed class MerchantRole : Entity<Guid>
{
    public const string WildcardPermission = "*";

    private MerchantRole(
        Guid id, Guid merchantId, string name, string? description, string? permissionCodesCsv, DateTimeOffset now) : base(id)
    {
        MerchantId = merchantId;
        Name = name;
        Description = description;
        PermissionCodesCsv = permissionCodesCsv;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private MerchantRole() : base(Guid.Empty)
    {
    }

    /// <summary>The tenant that owns this role. Immutable — a role never moves between merchants.</summary>
    public Guid MerchantId { get; private set; }

    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }

    /// <summary>Comma-separated permission codes — a small, bounded set, not worth a child table (same
    /// convention as the staff <c>Role</c> and <c>Merchant.AllowedIps</c>).</summary>
    public string? PermissionCodesCsv { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> PermissionCodes =>
        string.IsNullOrWhiteSpace(PermissionCodesCsv)
            ? []
            : PermissionCodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsWildcard => PermissionCodes.Contains(WildcardPermission, StringComparer.Ordinal);

    public static Result<MerchantRole> Create(
        Guid merchantId, string name, string? description, IReadOnlyCollection<string> permissionCodes, DateTimeOffset now)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<MerchantRole>(MerchantRoleErrors.MerchantRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<MerchantRole>(MerchantRoleErrors.NameRequired);

        return Result.Success(new MerchantRole(
            Guid.CreateVersion7(), merchantId, name.Trim(), Normalize(description), ToCsv(permissionCodes), now));
    }

    public Result SetPermissions(IReadOnlyCollection<string> permissionCodes, DateTimeOffset updatedAt)
    {
        PermissionCodesCsv = ToCsv(permissionCodes);
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    public Result UpdateDetails(string name, string? description, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(MerchantRoleErrors.NameRequired);

        Name = name.Trim();
        Description = Normalize(description);
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ToCsv(IReadOnlyCollection<string> codes)
    {
        var distinct = new HashSet<string>(codes.Select(c => c.Trim()).Where(c => c.Length > 0), StringComparer.Ordinal);
        return distinct.Count == 0 ? null : string.Join(',', distinct);
    }
}
