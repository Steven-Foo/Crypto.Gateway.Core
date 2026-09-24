using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// Stable dotted codes for every two-factor failure. A consumer branches on the code, never on the prose
/// (§ REQ-7) — the difference between "wrong code, try again", "you are locked out for a while" and "you
/// must finish enrolling" drives three completely different screens.
/// </summary>
public static class TwoFactorErrors
{
    public static readonly Error StaffUserRequired =
        Error.Validation("two_factor.staff_user_required", "A staff account is required.");

    public static readonly Error SecretRequired =
        Error.Validation("two_factor.secret_required", "A two-factor secret is required.");

    /// <summary>Unauthorized rather than Validation: the caller presented credentials and was refused for an
    /// AUTH reason. A SPA typically treats 401 as "show this on the sign-in form" and 400 as "my request is
    /// malformed", and mixing the two on one form sends a missing code down the wrong path.</summary>
    public static readonly Error CodeRequired =
        Error.Unauthorized("two_factor.code_required", "A six-digit authenticator code is required.");

    public static readonly Error InvalidCode =
        Error.Unauthorized("two_factor.invalid_code", "That authenticator code is not valid. Check your authenticator app and try again.");

    public static readonly Error LockedOut =
        Error.Unauthorized("two_factor.locked_out", "Too many incorrect codes. Try again in a few minutes.");

    /// <summary>The account has no active factor. On a guarded action this is a fail-closed refusal: an
    /// unenrolled operator is never waved through.</summary>
    public static readonly Error NotEnrolled =
        Error.Unauthorized("two_factor.not_enrolled", "This account has not set up two-factor authentication.");

    public static readonly Error EnrollmentRequired =
        Error.Unauthorized("two_factor.enrollment_required", "Finish setting up two-factor authentication before continuing.");

    /// <summary>Enrollment was never started, so there is nothing to confirm.</summary>
    public static readonly Error EnrollmentNotStarted =
        Error.NotFound("two_factor.enrollment_not_started", "Start two-factor setup before confirming a code.");

    /// <summary>Re-scanning onto a new device while already enrolled would let anyone holding a live session
    /// move the second factor to their own phone. Recovery goes through a recovery code or an admin reset.</summary>
    public static readonly Error AlreadyEnrolled =
        Error.Conflict("two_factor.already_enrolled", "Two-factor authentication is already set up for this account. Reset it first.");

    public static readonly Error RecoveryCodeAlreadyUsed =
        Error.Unauthorized("two_factor.recovery_code_already_used", "That recovery code has already been used.");

    /// <summary>A recovery code was presented for a guarded action. Recovery codes sign you in; they do not
    /// authorise money-touching actions.</summary>
    public static readonly Error RecoveryNotAcceptedForAction =
        Error.Unauthorized("two_factor.recovery_not_accepted", "Sign in with your authenticator app to perform this action.");

    public static readonly Error UpdatedByRequired =
        Error.Validation("two_factor.updated_by_required", "The staff member making the change must be recorded.");

    public static readonly Error InvalidActionCode =
        Error.Validation("two_factor.invalid_action_code", "An action code must not contain the '|' separator.");
}
