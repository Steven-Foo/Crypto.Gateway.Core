using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// A staff account's second factor — a TOTP secret bound to an authenticator app, plus the failure counters
/// that throttle guessing.
///
/// <para><b>Its own entity rather than columns on <see cref="StaffUser"/>.</b> The user row is read on every
/// login and every account-admin screen; putting the secret there materialises it on paths that have no
/// business holding it, and one careless projection leaks it into an API response. Keeping it separate makes
/// "load the secret" a deliberate act.</para>
///
/// <para><b>The secret is stored encrypted</b> (AES-256-GCM, via the module's cipher port) and never leaves
/// this system after enrollment: the provisioning URI is shown once, at the moment the QR is scanned, and
/// there is no endpoint that returns it again. Losing the device means enrolling again, which is the correct
/// cost.</para>
///
/// <para><b>Codes are verified, not consumed</b> — there is deliberately no replay guard here (see
/// <c>docs/two-factor-authentication.md</c> §6.1). A valid code stays usable for its own window so an
/// operator working through a queue is not paced at one action per thirty seconds. The compensating controls
/// are the narrow ±1-step skew, the lockout below, and the rule that a code is never written to a log.</para>
///
/// <para>No ledger impact and no signing keys (§10): a TOTP secret authenticates a person, it never signs a
/// transaction.</para>
/// </summary>
public sealed class StaffTwoFactor : Entity<Guid>
{
    /// <summary>
    /// Consecutive failures tolerated before the factor locks. Deliberately generous, because retry IS the
    /// designed behaviour: a mistyped or just-rolled code is the normal case, and a control that locks people
    /// out on ordinary typos is one they will route around. Not absent, though — six digits is 10^6, and an
    /// endpoint that accepts a guess per request is brute-forceable by a caller who already holds a session.
    /// </summary>
    public const int MaxFailedAttempts = 10;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private StaffTwoFactor(Guid id, Guid staffUserId, string secretCiphertext, DateTimeOffset now) : base(id)
    {
        StaffUserId = staffUserId;
        SecretCiphertext = secretCiphertext;
        Status = TwoFactorStatus.Pending;
        CreatedAt = now;
    }

    private StaffTwoFactor() : base(Guid.Empty)
    {
    }

    public Guid StaffUserId { get; private set; }

    /// <summary>The AES-256-GCM blob. Never logged, never returned over HTTP, never compared directly.</summary>
    public string SecretCiphertext { get; private set; } = null!;

    public TwoFactorStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EnrolledAt { get; private set; }

    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Only an <see cref="TwoFactorStatus.Active"/> factor counts. A <see cref="TwoFactorStatus.Pending"/>
    /// one — secret generated, QR shown, first code never entered — grants nothing: without this an
    /// interrupted enrollment leaves an account that believes it has 2FA and an operator who cannot produce
    /// a code for it.
    /// </summary>
    public bool IsEnrolled => Status == TwoFactorStatus.Active;

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && now < until;

    /// <summary>Begins enrollment. The caller has already generated and encrypted a fresh secret; this
    /// entity never sees plaintext.</summary>
    public static Result<StaffTwoFactor> Begin(Guid staffUserId, string secretCiphertext, DateTimeOffset now)
    {
        if (staffUserId == Guid.Empty)
            return Result.Failure<StaffTwoFactor>(TwoFactorErrors.StaffUserRequired);

        if (string.IsNullOrWhiteSpace(secretCiphertext))
            return Result.Failure<StaffTwoFactor>(TwoFactorErrors.SecretRequired);

        return Result.Success(new StaffTwoFactor(Guid.CreateVersion7(), staffUserId, secretCiphertext, now));
    }

    /// <summary>
    /// Replaces the secret and returns to <see cref="TwoFactorStatus.Pending"/> — used when someone restarts
    /// enrollment (scanned the QR on a device they then lost, closed the page, etc.).
    ///
    /// <para>Refused once the factor is Active: re-scanning to a new device while already enrolled would let
    /// anyone holding a live session silently move the second factor onto their own phone, which is the
    /// account-takeover path 2FA exists to close. An enrolled user who has lost their device goes through a
    /// recovery code or an admin reset, both of which are recorded.</para>
    /// </summary>
    public Result Restart(string secretCiphertext, DateTimeOffset now)
    {
        if (Status == TwoFactorStatus.Active)
            return Result.Failure(TwoFactorErrors.AlreadyEnrolled);

        if (string.IsNullOrWhiteSpace(secretCiphertext))
            return Result.Failure(TwoFactorErrors.SecretRequired);

        SecretCiphertext = secretCiphertext;
        Status = TwoFactorStatus.Pending;
        EnrolledAt = null;
        FailedAttempts = 0;
        LockedUntil = null;
        CreatedAt = now;
        return Result.Success();
    }

    /// <summary>Confirms enrollment once the first code has verified. Idempotent.</summary>
    public Result Confirm(DateTimeOffset now)
    {
        if (Status == TwoFactorStatus.Active)
            return Result.Success();

        Status = TwoFactorStatus.Active;
        EnrolledAt = now;
        FailedAttempts = 0;
        LockedUntil = null;
        return Result.Success();
    }

    /// <summary>Records a successful verification: clears the failure streak. Nothing about the code itself
    /// is stored — see the note on replay in the type summary.</summary>
    public void RecordSuccess()
    {
        FailedAttempts = 0;
        LockedUntil = null;
    }

    /// <summary>Records a failed verification, locking the factor once the streak reaches
    /// <see cref="MaxFailedAttempts"/>. Returns true if this attempt caused the lock, so the caller can audit
    /// the transition rather than every individual miss.</summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        FailedAttempts++;

        if (FailedAttempts < MaxFailedAttempts)
            return false;

        LockedUntil = now + LockoutDuration;
        FailedAttempts = 0; // the lock is the consequence; the streak restarts after it expires
        return true;
    }

    /// <summary>
    /// Clears the factor entirely — an admin reset, or a user re-enrolling after recovery. The row is kept
    /// (rather than deleted) so the history of the account's enrollment remains visible; the account is
    /// simply unenrolled again and will be forced through enrollment at its next login.
    /// </summary>
    public Result Disable()
    {
        Status = TwoFactorStatus.Disabled;
        EnrolledAt = null;
        FailedAttempts = 0;
        LockedUntil = null;
        return Result.Success();
    }
}

public enum TwoFactorStatus
{
    /// <summary>Secret generated and shown, first code not yet verified. Grants nothing.</summary>
    Pending = 1,

    /// <summary>Enrolled and in force.</summary>
    Active = 2,

    /// <summary>Cleared by an admin reset or a re-enrollment. The account must enroll again to log in.</summary>
    Disabled = 3,
}
