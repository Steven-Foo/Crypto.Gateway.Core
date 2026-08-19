using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// A staff/operator account for the Ops-facing surface — distinct from <c>Merchant</c> (an external
/// partner) and from any future merchant-portal login. Access is a reference to a DB-defined
/// <see cref="Role"/> (§ Role — replaces the old flat <c>StaffRole</c> enum so new roles can be added
/// without a redeploy), not a hardcoded value on the user itself.
/// </summary>
public sealed class StaffUser : Entity<Guid>
{
    private StaffUser(Guid id, string username, string passwordHash, Guid roleId, DateTimeOffset now) : base(id)
    {
        Username = username;
        PasswordHash = passwordHash;
        RoleId = roleId;
        Status = StaffUserStatus.Active;
        CreatedAt = now;
    }

    private StaffUser() : base(Guid.Empty)
    {
    }

    public string Username { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public Guid RoleId { get; private set; }
    public StaffUserStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool CanLogIn => Status == StaffUserStatus.Active;

    public static Result<StaffUser> Create(string username, string passwordHash, Guid roleId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<StaffUser>(StaffUserErrors.UsernameRequired);

        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure<StaffUser>(StaffUserErrors.PasswordHashRequired);

        if (roleId == Guid.Empty)
            return Result.Failure<StaffUser>(StaffUserErrors.RoleRequired);

        return Result.Success(new StaffUser(Guid.CreateVersion7(), username.Trim(), passwordHash, roleId, now));
    }

    public Result ChangeRole(Guid roleId)
    {
        if (roleId == Guid.Empty)
            return Result.Failure(StaffUserErrors.RoleRequired);

        RoleId = roleId;
        return Result.Success();
    }

    public Result ResetPassword(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure(StaffUserErrors.PasswordHashRequired);

        PasswordHash = passwordHash;
        return Result.Success();
    }

    /// <summary>Reversible — a disabled account can be re-activated. Login is refused while disabled
    /// (<see cref="CanLogIn"/>); existing sessions are not proactively revoked (a later hardening step).</summary>
    public Result SetStatus(StaffUserStatus status)
    {
        Status = status;
        return Result.Success();
    }
}

public enum StaffUserStatus
{
    Active = 1,
    Disabled = 2,
}
