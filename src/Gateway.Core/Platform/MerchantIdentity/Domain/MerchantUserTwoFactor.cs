using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

/// <summary>
/// A merchant-portal user's second factor — the tenant-side mirror of the staff module's
/// <c>StaffTwoFactor</c>.
///
/// <para><b>Deliberately a separate type in a separate module, not a shared one.</b> The two identity
/// modules must stay independently extractable (§4.5); they share the SharedKernel primitives (the TOTP
/// algorithm, the AES-GCM secret box, the password hash) and nothing else, exactly as they already do for
/// <c>Pbkdf2PasswordHash</c> and <c>OpaqueToken</c>. The duplication is in the entity shape, which is cheap;
/// the security-critical logic is shared.</para>
///
/// <para><b>This phase is LOGIN ONLY.</b> The portal has no guarded actions and no policy table — a merchant
/// user proves themselves once, at sign-in. When per-action guarding is wanted there (payout approval is the
/// obvious first), it transplants the staff module's catalog pattern with a per-tenant rather than
/// platform-wide policy, which is its own piece of work.</para>
/// </summary>
public sealed class MerchantUserTwoFactor : Entity<Guid>
{
    public const int MaxFailedAttempts = 10;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private MerchantUserTwoFactor(
        Guid id, Guid merchantUserId, Guid merchantId, string secretCiphertext, DateTimeOffset now) : base(id)
    {
        MerchantUserId = merchantUserId;
        MerchantId = merchantId;
        SecretCiphertext = secretCiphertext;
        Status = MerchantTwoFactorStatus.Pending;
        CreatedAt = now;
    }

    private MerchantUserTwoFactor() : base(Guid.Empty)
    {
    }

    public Guid MerchantUserId { get; private set; }

    /// <summary>The owning tenant, carried so every read can be filtered by it — a merchant must never be
    /// able to reach another's factor even holding an exact id.</summary>
    public Guid MerchantId { get; private set; }

    /// <summary>AES-256-GCM. Never logged, never returned over HTTP after enrollment.</summary>
    public string SecretCiphertext { get; private set; } = null!;

    public MerchantTwoFactorStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EnrolledAt { get; private set; }

    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>Only an Active factor counts — a Pending one (QR shown, first code never entered) grants
    /// nothing, or an interrupted setup would leave an account believing it is protected.</summary>
    public bool IsEnrolled => Status == MerchantTwoFactorStatus.Active;

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && now < until;

    public static Result<MerchantUserTwoFactor> Begin(
        Guid merchantUserId, Guid merchantId, string secretCiphertext, DateTimeOffset now)
    {
        if (merchantUserId == Guid.Empty || merchantId == Guid.Empty)
            return Result.Failure<MerchantUserTwoFactor>(MerchantTwoFactorErrors.UserRequired);

        if (string.IsNullOrWhiteSpace(secretCiphertext))
            return Result.Failure<MerchantUserTwoFactor>(MerchantTwoFactorErrors.SecretRequired);

        return Result.Success(
            new MerchantUserTwoFactor(Guid.CreateVersion7(), merchantUserId, merchantId, secretCiphertext, now));
    }

    /// <summary>Restarts an unfinished enrollment. Refused once Active: re-scanning onto a new device from a
    /// live session is the account-takeover path, so recovery goes through a recovery code or a merchant
    /// admin reset, both of which are recorded.</summary>
    public Result Restart(string secretCiphertext, DateTimeOffset now)
    {
        if (Status == MerchantTwoFactorStatus.Active)
            return Result.Failure(MerchantTwoFactorErrors.AlreadyEnrolled);

        if (string.IsNullOrWhiteSpace(secretCiphertext))
            return Result.Failure(MerchantTwoFactorErrors.SecretRequired);

        SecretCiphertext = secretCiphertext;
        Status = MerchantTwoFactorStatus.Pending;
        EnrolledAt = null;
        FailedAttempts = 0;
        LockedUntil = null;
        CreatedAt = now;
        return Result.Success();
    }

    public Result Confirm(DateTimeOffset now)
    {
        if (Status == MerchantTwoFactorStatus.Active)
            return Result.Success();

        Status = MerchantTwoFactorStatus.Active;
        EnrolledAt = now;
        FailedAttempts = 0;
        LockedUntil = null;
        return Result.Success();
    }

    public void RecordSuccess()
    {
        FailedAttempts = 0;
        LockedUntil = null;
    }

    /// <summary>Returns true if this attempt caused the lock, so the caller can log the transition rather
    /// than every individual miss.</summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        FailedAttempts++;

        if (FailedAttempts < MaxFailedAttempts)
            return false;

        LockedUntil = now + LockoutDuration;
        FailedAttempts = 0;
        return true;
    }

    public Result Disable()
    {
        Status = MerchantTwoFactorStatus.Disabled;
        EnrolledAt = null;
        FailedAttempts = 0;
        LockedUntil = null;
        return Result.Success();
    }
}

public enum MerchantTwoFactorStatus
{
    Pending = 1,
    Active = 2,
    Disabled = 3,
}

/// <summary>One single-use fallback code. Hashed like a password — the server only ever checks it, never
/// reproduces it.</summary>
public sealed class MerchantUserRecoveryCode : Entity<Guid>
{
    public const int BatchSize = 10;

    private MerchantUserRecoveryCode(
        Guid id, Guid merchantUserId, Guid merchantId, string codeHash, DateTimeOffset now) : base(id)
    {
        MerchantUserId = merchantUserId;
        MerchantId = merchantId;
        CodeHash = codeHash;
        CreatedAt = now;
    }

    private MerchantUserRecoveryCode() : base(Guid.Empty)
    {
    }

    public Guid MerchantUserId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string CodeHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsUsable => UsedAt is null;

    public static MerchantUserRecoveryCode Issue(
        Guid merchantUserId, Guid merchantId, string codeHash, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), merchantUserId, merchantId, codeHash, now);

    public Result Consume(DateTimeOffset now)
    {
        if (UsedAt is not null)
            return Result.Failure(MerchantTwoFactorErrors.RecoveryCodeAlreadyUsed);

        UsedAt = now;
        return Result.Success();
    }
}

/// <summary>
/// Stable dotted codes, reaching the client as the response's <c>errorCode</c> (the portal host emits one on
/// every failure, like Ops — an older note in the integration doc claiming otherwise is out of date).
/// A SPA cannot tell "enroll now" from "wrong code" out of display prose, and those are different screens.
/// </summary>
public static class MerchantTwoFactorErrors
{
    public static readonly Error UserRequired =
        Error.Validation("portal_two_factor.user_required", "A portal account is required.");

    public static readonly Error SecretRequired =
        Error.Validation("portal_two_factor.secret_required", "A two-factor secret is required.");

    /// <summary>Unauthorized rather than Validation: a missing code at sign-in is an auth refusal, not a
    /// malformed request, and a SPA routes those two to different places.</summary>
    public static readonly Error CodeRequired =
        Error.Unauthorized("portal_two_factor.code_required", "A six-digit authenticator code is required.");

    public static readonly Error InvalidCode =
        Error.Unauthorized("portal_two_factor.invalid_code", "That authenticator code is not valid. Check your authenticator app and try again.");

    public static readonly Error LockedOut =
        Error.Unauthorized("portal_two_factor.locked_out", "Too many incorrect codes. Try again in a few minutes.");

    public static readonly Error NotEnrolled =
        Error.Unauthorized("portal_two_factor.not_enrolled", "This account has not set up two-factor authentication.");

    public static readonly Error EnrollmentRequired =
        Error.Unauthorized("portal_two_factor.enrollment_required", "Finish setting up two-factor authentication before continuing.");

    public static readonly Error EnrollmentNotStarted =
        Error.NotFound("portal_two_factor.enrollment_not_started", "Start two-factor setup before confirming a code.");

    public static readonly Error AlreadyEnrolled =
        Error.Conflict("portal_two_factor.already_enrolled", "Two-factor authentication is already set up for this account. Reset it first.");

    public static readonly Error RecoveryCodeAlreadyUsed =
        Error.Unauthorized("portal_two_factor.recovery_code_already_used", "That recovery code has already been used.");
}
