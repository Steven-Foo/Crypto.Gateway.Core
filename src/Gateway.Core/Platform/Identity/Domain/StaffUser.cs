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
    private StaffUser(
        Guid id, string username, string passwordHash, Guid roleId, bool requireTwoFactor, DateTimeOffset now) : base(id)
    {
        Username = username;
        PasswordHash = passwordHash;
        RoleId = roleId;
        RequireTwoFactor = requireTwoFactor;
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

    /// <summary>The live master switch, set at creation and flippable anytime after (§ account management —
    /// "force bind, or bind later"), independent of whether a factor is actually bound. False suppresses 2FA
    /// entirely for this account — at login AND on every guarded action — even if a factor was bound earlier
    /// and never unbound; flipping it back on demands a code again immediately, no re-enrollment needed,
    /// because the bound secret was never touched. Distinct from bind status (<c>StaffTwoFactor.Status</c>),
    /// which this entity does not reference at all (§4.5 — Identity's two concerns: "must this account prove
    /// itself" and "has it currently got something to prove itself with" — live in different places on
    /// purpose, so one can change without touching the other).</summary>
    public bool RequireTwoFactor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool CanLogIn => Status == StaffUserStatus.Active;

    public static Result<StaffUser> Create(
        string username, string passwordHash, Guid roleId, bool requireTwoFactor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<StaffUser>(StaffUserErrors.UsernameRequired);

        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure<StaffUser>(StaffUserErrors.PasswordHashRequired);

        if (roleId == Guid.Empty)
            return Result.Failure<StaffUser>(StaffUserErrors.RoleRequired);

        return Result.Success(new StaffUser(
            Guid.CreateVersion7(), username.Trim(), passwordHash, roleId, requireTwoFactor, now));
    }

    /// <summary>Flips the live switch. Deliberately independent of bind status — turning it off never touches
    /// <c>StaffTwoFactor</c>, and turning it back on never demands re-enrollment (the bound secret, if any,
    /// was never disturbed).</summary>
    public Result SetRequireTwoFactor(bool requireTwoFactor)
    {
        RequireTwoFactor = requireTwoFactor;
        return Result.Success();
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
