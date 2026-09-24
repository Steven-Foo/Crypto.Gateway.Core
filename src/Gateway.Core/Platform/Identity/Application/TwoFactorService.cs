using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

/// <summary>What a newly started enrollment hands back. Shown ONCE — there is no endpoint that returns the
/// secret again, so a lost setup means starting over.</summary>
/// <param name="SecretBase32">For manual entry when a camera is unavailable.</param>
/// <param name="ProvisioningUri">The QR payload an authenticator scans.</param>
public sealed record TwoFactorEnrollment(string SecretBase32, string ProvisioningUri);

/// <summary>Whether an account has a factor, and what state it is in. Carries no secret.</summary>
public sealed record TwoFactorStatusView(
    bool Enrolled,
    TwoFactorStatus? Status,
    DateTimeOffset? EnrolledAt,
    int RecoveryCodesRemaining,
    bool LockedOut);

public interface ITwoFactorService
{
    Task<TwoFactorStatusView> GetStatusAsync(Guid staffUserId, CancellationToken cancellationToken = default);

    /// <summary>True when the account holds an ACTIVE factor. The one question the forced-enrollment gate
    /// and the guarded-action filter both ask.</summary>
    Task<bool> IsEnrolledAsync(Guid staffUserId, CancellationToken cancellationToken = default);

    /// <summary>Generates a secret and returns it once. Re-callable while enrollment is unfinished (people
    /// close the page); refused once the factor is Active.</summary>
    Task<Result<TwoFactorEnrollment>> BeginEnrollmentAsync(
        Guid staffUserId, string username, CancellationToken cancellationToken = default);

    /// <summary>Verifies the first code and activates the factor, returning the recovery codes once.</summary>
    Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        Guid staffUserId, string? code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a code against the account's active factor. Used by login and by the guarded-action filter.
    ///
    /// <para><b>Verifies; does not consume.</b> A valid code stays usable for its own window, so an operator
    /// working a queue is not paced at one action per thirty seconds (docs §6.1).</para>
    /// </summary>
    Task<Result> VerifyAsync(Guid staffUserId, string? code, CancellationToken cancellationToken = default);

    /// <summary>Spends a single-use recovery code. Login only — never accepted for a guarded action.</summary>
    Task<Result> RedeemRecoveryCodeAsync(Guid staffUserId, string? code, CancellationToken cancellationToken = default);

    /// <summary>Issues a fresh batch, invalidating the previous one. Requires an already-active factor.</summary>
    Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid staffUserId, CancellationToken cancellationToken = default);

    /// <summary>Clears an account's factor — an admin reset. The account is unenrolled and will be forced
    /// back through enrollment at its next login.</summary>
    Task<Result> ResetAsync(Guid staffUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything the second factor does, in one service: the operations share a small dependency set and none
/// of them is large enough alone to earn its own file (the same judgement <see cref="StaffAuthService"/>
/// makes about login/logout/validate).
///
/// <para>The secret never leaves this class in plaintext. It is decrypted into a local, used to compute a
/// comparison, and dropped; nothing returns it, logs it, or stores it unencrypted.</para>
/// </summary>
public sealed class TwoFactorService(
    IStaffTwoFactorRepository repository,
    ITwoFactorSecretCipher cipher,
    IOptions<TwoFactorOptions> options,
    TimeProvider clock,
    ILogger<TwoFactorService> logger) : ITwoFactorService
{
    private readonly TwoFactorOptions _options = options.Value;

    public async Task<TwoFactorStatusView> GetStatusAsync(Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null)
            return new TwoFactorStatusView(false, null, null, 0, false);

        var remaining = factor.IsEnrolled
            ? (await repository.ListRecoveryCodesAsync(staffUserId, unusedOnly: true, cancellationToken)).Count
            : 0;

        return new TwoFactorStatusView(
            factor.IsEnrolled, factor.Status, factor.EnrolledAt, remaining, factor.IsLockedOut(clock.GetUtcNow()));
    }

    public async Task<bool> IsEnrolledAsync(Guid staffUserId, CancellationToken cancellationToken = default) =>
        (await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken))?.IsEnrolled == true;

    public async Task<Result<TwoFactorEnrollment>> BeginEnrollmentAsync(
        Guid staffUserId, string username, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var secret = Totp.GenerateSecret();
        var ciphertext = cipher.Protect(secret);

        var existing = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (existing is null)
        {
            var created = StaffTwoFactor.Begin(staffUserId, ciphertext, now);
            if (created.IsFailure)
                return Result.Failure<TwoFactorEnrollment>(created.Error!);

            repository.Add(created.Value);
        }
        else
        {
            // Refused when already Active: re-scanning onto a new device from a live session is how an
            // account is taken over, so recovery goes through a recovery code or an admin reset instead.
            var restarted = existing.Restart(ciphertext, now);
            if (restarted.IsFailure)
                return Result.Failure<TwoFactorEnrollment>(restarted.Error!);
        }

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Two-factor enrollment started for staff account {StaffUserId}.", staffUserId);

        return Result.Success(new TwoFactorEnrollment(
            Totp.ToBase32(secret),
            Totp.ProvisioningUri(_options.Issuer, username, secret)));
    }

    public async Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        Guid staffUserId, string? code, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null)
            return Result.Failure<IReadOnlyList<string>>(TwoFactorErrors.EnrollmentNotStarted);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure<IReadOnlyList<string>>(TwoFactorErrors.LockedOut);

        if (!VerifyCode(factor, code, now))
        {
            await RecordFailureAsync(factor, staffUserId, "confirming enrollment", cancellationToken);
            return Result.Failure<IReadOnlyList<string>>(TwoFactorErrors.InvalidCode);
        }

        factor.Confirm(now);
        var codes = await IssueRecoveryCodesAsync(staffUserId, now, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Two-factor enrollment confirmed for staff account {StaffUserId}.", staffUserId);

        return Result.Success(codes);
    }

    public async Task<Result> VerifyAsync(Guid staffUserId, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure(TwoFactorErrors.CodeRequired);

        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure(TwoFactorErrors.NotEnrolled);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure(TwoFactorErrors.LockedOut);

        if (!VerifyCode(factor, code, now))
        {
            await RecordFailureAsync(factor, staffUserId, "verifying a code", cancellationToken);
            return Result.Failure(TwoFactorErrors.InvalidCode);
        }

        // Clearing the failure streak is a write, but only when the streak is non-zero — a successful code on
        // a clean account must not cost a database round trip per guarded action.
        if (factor.FailedAttempts > 0 || factor.LockedUntil is not null)
        {
            factor.RecordSuccess();
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> RedeemRecoveryCodeAsync(
        Guid staffUserId, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure(TwoFactorErrors.CodeRequired);

        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure(TwoFactorErrors.NotEnrolled);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure(TwoFactorErrors.LockedOut);

        var normalized = NormalizeRecoveryCode(code);
        var candidates = await repository.ListRecoveryCodesAsync(staffUserId, unusedOnly: true, cancellationToken);

        // Every candidate is checked, with no early exit, so the time taken does not reveal how many codes
        // remain or where in the list a match sat.
        StaffRecoveryCode? matched = null;
        foreach (var candidate in candidates)
        {
            if (Pbkdf2PasswordHash.Verify(normalized, candidate.CodeHash))
                matched ??= candidate;
        }

        if (matched is null)
        {
            await RecordFailureAsync(factor, staffUserId, "redeeming a recovery code", cancellationToken);
            return Result.Failure(TwoFactorErrors.InvalidCode);
        }

        var consumed = matched.Consume(now);
        if (consumed.IsFailure)
            return consumed;

        factor.RecordSuccess();
        await repository.SaveChangesAsync(cancellationToken);

        // Warning, not information: a recovery code being used is unusual and worth someone noticing, and
        // running out of them locks the account out on the next lost device.
        logger.LogWarning(
            "Staff account {StaffUserId} signed in with a recovery code; {Remaining} remaining.",
            staffUserId, candidates.Count - 1);

        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure<IReadOnlyList<string>>(TwoFactorErrors.NotEnrolled);

        var codes = await IssueRecoveryCodesAsync(staffUserId, clock.GetUtcNow(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Recovery codes regenerated for staff account {StaffUserId}.", staffUserId);

        return Result.Success(codes);
    }

    public async Task<Result> ResetAsync(Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindByStaffUserIdAsync(staffUserId, cancellationToken);
        if (factor is null)
            return Result.Success(); // nothing enrolled — a reset is idempotent, not an error

        factor.Disable();

        // The old codes go with the old factor. Leaving them live would mean a reset that removes the
        // authenticator but silently keeps ten printed ways in.
        var codes = await repository.ListRecoveryCodesAsync(staffUserId, unusedOnly: false, cancellationToken);
        repository.RemoveRecoveryCodes(codes);

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Two-factor authentication was reset for staff account {StaffUserId}.", staffUserId);

        return Result.Success();
    }

    private bool VerifyCode(StaffTwoFactor factor, string? code, DateTimeOffset now)
    {
        var secret = cipher.Unprotect(factor.SecretCiphertext);
        try
        {
            return Totp.Verify(secret, code, now);
        }
        finally
        {
            // The plaintext secret lives no longer than the comparison that needed it.
            Array.Clear(secret);
        }
    }

    private async Task RecordFailureAsync(
        StaffTwoFactor factor, Guid staffUserId, string what, CancellationToken cancellationToken)
    {
        var locked = factor.RecordFailure(clock.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        if (locked)
        {
            logger.LogWarning(
                "Staff account {StaffUserId} locked out of two-factor after {Attempts} consecutive failures while {What}.",
                staffUserId, StaffTwoFactor.MaxFailedAttempts, what);
        }
    }

    private async Task<IReadOnlyList<string>> IssueRecoveryCodesAsync(
        Guid staffUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await repository.ListRecoveryCodesAsync(staffUserId, unusedOnly: false, cancellationToken);
        repository.RemoveRecoveryCodes(existing);

        var plaintext = new List<string>(StaffRecoveryCode.BatchSize);
        var entities = new List<StaffRecoveryCode>(StaffRecoveryCode.BatchSize);

        for (var i = 0; i < StaffRecoveryCode.BatchSize; i++)
        {
            var code = TemporaryPassword.Generate();
            plaintext.Add(code);
            entities.Add(StaffRecoveryCode.Issue(staffUserId, Pbkdf2PasswordHash.Hash(NormalizeRecoveryCode(code)), now));
        }

        repository.AddRecoveryCodes(entities);
        return plaintext;
    }

    /// <summary>Recovery codes are transcribed by hand from a printout, so case and separators are forgiven;
    /// normalising on both sides means the stored hash and the presented code agree.</summary>
    private static string NormalizeRecoveryCode(string code) =>
        code.Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
}
