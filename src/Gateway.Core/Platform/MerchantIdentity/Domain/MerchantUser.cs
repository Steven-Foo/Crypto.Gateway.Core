using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

/// <summary>
/// A login account for the <b>merchant portal</b> — a person who works for a merchant partner. Distinct from
/// <c>Platform.Identity.StaffUser</c> (our own operators) and from the <c>Merchant</c> entity itself: this is a
/// human who signs in to view/act on exactly one merchant's data. The <see cref="MerchantId"/> binding is the
/// tenant boundary — it is stamped onto every session at login and is the ONLY source of tenant scope for the
/// portal (a request never names its own merchant, § tenant-isolation).
///
/// <para>Access is a reference to a per-tenant <see cref="MerchantRole"/> whose codes are snapshotted onto the
/// session at login. <see cref="RoleId"/> is nullable and <b>fail-closed</b>: an account with no role resolves
/// to an empty permission set, so it can sign in but do nothing — never a silent grant.</para>
/// </summary>
public sealed class MerchantUser : Entity<Guid>
{
    private MerchantUser(
        Guid id, Guid merchantId, string username, string displayName, string passwordHash, Guid? roleId,
        bool mustChangePassword, bool isPrimary, bool requireTwoFactor, DateTimeOffset now) : base(id)
    {
        MerchantId = merchantId;
        Username = username;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        RoleId = roleId;
        MustChangePassword = mustChangePassword;
        IsPrimary = isPrimary;
        RequireTwoFactor = requireTwoFactor;
        Status = MerchantUserStatus.Active;
        CreatedAt = now;
    }

    private MerchantUser() : base(Guid.Empty)
    {
    }

    /// <summary>The single merchant this account belongs to — the tenant boundary. Immutable.</summary>
    public Guid MerchantId { get; private set; }

    /// <summary>Globally unique across all merchants (like an email), so login needs only a username — no
    /// merchant selector — and still resolves to exactly one tenant.</summary>
    public string Username { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;

    /// <summary>The tenant-owned role granting this account's portal permissions. Null ⇒ no permissions at all
    /// (fail-closed), never an implicit grant.</summary>
    public Guid? RoleId { get; private set; }

    /// <summary>Set when an admin creates the account or resets its password (the value handed over is a
    /// generated one-time password); cleared once the user sets their own. The portal uses it to force a
    /// change-password step at first login.</summary>
    public bool MustChangePassword { get; private set; }

    /// <summary>The merchant's original super-admin — the one account created when the merchant had zero
    /// accounts (see <c>MerchantAccountService.CreateAsync</c>), set once here and never reassigned. A
    /// filtered unique index (<c>MerchantId</c> WHERE <c>IsPrimary = 1</c>) guarantees at most one per
    /// merchant at the database level, not just by application convention. It is the ONE account staff may
    /// reset on the merchant's behalf (§ platform-side password reset is primary-only by design) and can
    /// never itself be <see cref="SetStatus"/>-disabled — disabling it would mean silently redefining what
    /// "the merchant's super-admin" refers to, which must be a deliberate act, not a side effect of an
    /// ordinary account-disable action.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>The live master switch (§ StaffUser.RequireTwoFactor — identical concept, mirrored for the
    /// portal): set at creation, flippable anytime after by another portal admin, independent of whether a
    /// factor is actually bound. False suppresses 2FA entirely for this account, even one already enrolled;
    /// flipping it back on demands a code again immediately since the bound secret was never touched.</summary>
    public bool RequireTwoFactor { get; private set; }

    public MerchantUserStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool CanLogIn => Status == MerchantUserStatus.Active;

    public static Result<MerchantUser> Create(
        Guid merchantId, string username, string displayName, string passwordHash, Guid? roleId,
        bool mustChangePassword, bool isPrimary, bool requireTwoFactor, DateTimeOffset now)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<MerchantUser>(MerchantUserErrors.MerchantRequired);

        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<MerchantUser>(MerchantUserErrors.UsernameRequired);

        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure<MerchantUser>(MerchantUserErrors.PasswordHashRequired);

        var name = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();
        return Result.Success(new MerchantUser(
            Guid.CreateVersion7(), merchantId, username.Trim(), name, passwordHash, roleId, mustChangePassword,
            isPrimary, requireTwoFactor, now));
    }

    /// <summary>Flips the live switch. Independent of bind status — see <see cref="RequireTwoFactor"/>.</summary>
    public Result SetRequireTwoFactor(bool requireTwoFactor)
    {
        RequireTwoFactor = requireTwoFactor;
        return Result.Success();
    }

    /// <summary>Assigns (or clears) the account's role. The caller must have verified the role belongs to the
    /// SAME merchant — cross-tenant assignment is a tenant-isolation break, not a validation nicety.</summary>
    public Result AssignRole(Guid? roleId)
    {
        RoleId = roleId;
        return Result.Success();
    }

    /// <summary>An admin-driven reset — hands over a generated one-time password, so the user must change it.</summary>
    public Result ResetPassword(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure(MerchantUserErrors.PasswordHashRequired);

        PasswordHash = passwordHash;
        MustChangePassword = true;
        return Result.Success();
    }

    /// <summary>The user setting their own password — clears the forced-change flag.</summary>
    public Result ChangeOwnPassword(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            return Result.Failure(MerchantUserErrors.PasswordHashRequired);

        PasswordHash = passwordHash;
        MustChangePassword = false;
        return Result.Success();
    }

    public Result Rename(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            DisplayName = displayName.Trim();
        return Result.Success();
    }

    /// <summary>Reversible. Login is refused while disabled (<see cref="CanLogIn"/>); existing sessions are not
    /// proactively revoked (a later hardening step, mirroring StaffUser). Refuses to disable the merchant's
    /// <see cref="IsPrimary"/> account — see that property's doc for why.</summary>
    public Result SetStatus(MerchantUserStatus status)
    {
        if (IsPrimary && status == MerchantUserStatus.Disabled)
            return Result.Failure(MerchantUserErrors.CannotDisablePrimaryAccount);

        Status = status;
        return Result.Success();
    }
}

public enum MerchantUserStatus
{
    Active = 1,
    Disabled = 2,
}
