using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// A named, DB-defined set of permission codes — replaces the old flat <c>StaffRole</c> enum so new roles
/// can be added without a redeploy. A permission code is an opaque string owned by the host that enforces it
/// (e.g. <c>ops.withdrawals.approve</c>); Identity never interprets what a code means, only stores and
/// snapshots the set (§4.5 — Identity must not know Merchant/Withdrawal/etc. exist). The single code
/// <c>"*"</c> is a wildcard meaning every permission — used for the built-in Admin/dev-seed role so the
/// system always has one account that cannot be locked out.
/// </summary>
public sealed class Role : Entity<Guid>
{
    public const string WildcardPermission = "*";

    private Role(Guid id, string name, string? description, string? permissionCodesCsv, DateTimeOffset now) : base(id)
    {
        Name = name;
        Description = description;
        PermissionCodesCsv = permissionCodesCsv;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Role() : base(Guid.Empty)
    {
    }

    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }

    /// <summary>Comma-separated permission codes — small, bounded set, not worth a child table. Same
    /// storage convention as <c>Merchant.AllowedIps</c>.</summary>
    public string? PermissionCodesCsv { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> PermissionCodes =>
        string.IsNullOrWhiteSpace(PermissionCodesCsv)
            ? []
            : PermissionCodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True if this role's set includes the wildcard — grants every permission, present or future.</summary>
    public bool IsWildcard => PermissionCodes.Contains(WildcardPermission, StringComparer.Ordinal);

    public static Result<Role> Create(
        string name, string? description, IReadOnlyCollection<string> permissionCodes, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Role>(RoleErrors.NameRequired);

        var csv = ToCsv(permissionCodes);
        return Result.Success(new Role(Guid.CreateVersion7(), name.Trim(), Normalize(description), csv, now));
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
            return Result.Failure(RoleErrors.NameRequired);

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
