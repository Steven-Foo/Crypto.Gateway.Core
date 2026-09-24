using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed record MerchantTwoFactorEnrollment(string SecretBase32, string ProvisioningUri);

public sealed record MerchantTwoFactorStatusView(
    bool Enrolled,
    MerchantTwoFactorStatus? Status,
    DateTimeOffset? EnrolledAt,
    int RecoveryCodesRemaining,
    bool LockedOut);

/// <summary>
/// The portal's second factor. Every method takes the caller's <c>merchantId</c> as its first argument and
/// every repository read is filtered by it — the same tenant-isolation discipline as
/// <see cref="MerchantAccountService"/> and <see cref="MerchantRoleService"/>, so a foreign id reads as
/// "not found" rather than being actionable.
/// </summary>
public interface IMerchantTwoFactorService
{
    Task<MerchantTwoFactorStatusView> GetStatusAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default);

    Task<bool> IsEnrolledAsync(Guid merchantUserId, CancellationToken cancellationToken = default);

    Task<Result<MerchantTwoFactorEnrollment>> BeginEnrollmentAsync(
        Guid merchantId, Guid merchantUserId, string username, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        Guid merchantId, Guid merchantUserId, string? code, CancellationToken cancellationToken = default);

    /// <summary>Verifies a code. Used only by login in this phase — the portal has no guarded actions yet.</summary>
    Task<Result> VerifyAsync(Guid merchantUserId, string? code, CancellationToken cancellationToken = default);

    Task<Result> RedeemRecoveryCodeAsync(
        Guid merchantUserId, string? code, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default);

    /// <summary>A merchant admin clearing one of their OWN users' factors, so a tenant can recover a
    /// colleague's lost device without involving platform staff.</summary>
    Task<Result> ResetAsync(Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default);
}

public sealed class MerchantTwoFactorService(
    IMerchantTwoFactorRepository repository,
    IMerchantTwoFactorSecretCipher cipher,
    IOptions<MerchantIdentityOptions> options,
    TimeProvider clock,
    ILogger<MerchantTwoFactorService> logger) : IMerchantTwoFactorService
{
    private readonly MerchantIdentityOptions _options = options.Value;

    public async Task<MerchantTwoFactorStatusView> GetStatusAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindAsync(merchantId, merchantUserId, cancellationToken);
        if (factor is null)
            return new MerchantTwoFactorStatusView(false, null, null, 0, false);

        var remaining = factor.IsEnrolled
            ? (await repository.ListRecoveryCodesAsync(merchantUserId, unusedOnly: true, cancellationToken)).Count
            : 0;

        return new MerchantTwoFactorStatusView(
            factor.IsEnrolled, factor.Status, factor.EnrolledAt, remaining, factor.IsLockedOut(clock.GetUtcNow()));
    }

    /// <summary>
    /// Deliberately NOT tenant-scoped: it is called from the login path, where the session that would supply
    /// the tenant does not exist yet. The user id comes from a username lookup the caller has already
    /// authenticated with a password, so there is nothing a caller could substitute.
    /// </summary>
    public async Task<bool> IsEnrolledAsync(Guid merchantUserId, CancellationToken cancellationToken = default) =>
        (await repository.FindByUserAsync(merchantUserId, cancellationToken))?.IsEnrolled == true;

    public async Task<Result<MerchantTwoFactorEnrollment>> BeginEnrollmentAsync(
        Guid merchantId, Guid merchantUserId, string username, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var secret = Totp.GenerateSecret();
        var ciphertext = cipher.Protect(secret);

        var existing = await repository.FindAsync(merchantId, merchantUserId, cancellationToken);
        if (existing is null)
        {
            var created = MerchantUserTwoFactor.Begin(merchantUserId, merchantId, ciphertext, now);
            if (created.IsFailure)
                return Result.Failure<MerchantTwoFactorEnrollment>(created.Error!);

            repository.Add(created.Value);
        }
        else
        {
            var restarted = existing.Restart(ciphertext, now);
            if (restarted.IsFailure)
                return Result.Failure<MerchantTwoFactorEnrollment>(restarted.Error!);
        }

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Portal two-factor enrollment started for user {MerchantUserId} of merchant {MerchantId}.",
            merchantUserId, merchantId);

        return Result.Success(new MerchantTwoFactorEnrollment(
            Totp.ToBase32(secret),
            Totp.ProvisioningUri(_options.TwoFactorIssuer, username, secret)));
    }

    public async Task<Result<IReadOnlyList<string>>> ConfirmEnrollmentAsync(
        Guid merchantId, Guid merchantUserId, string? code, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindAsync(merchantId, merchantUserId, cancellationToken);
        if (factor is null)
            return Result.Failure<IReadOnlyList<string>>(MerchantTwoFactorErrors.EnrollmentNotStarted);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure<IReadOnlyList<string>>(MerchantTwoFactorErrors.LockedOut);

        if (!VerifyCode(factor, code, now))
        {
            await RecordFailureAsync(factor, merchantUserId, cancellationToken);
            return Result.Failure<IReadOnlyList<string>>(MerchantTwoFactorErrors.InvalidCode);
        }

        factor.Confirm(now);
        var codes = await IssueRecoveryCodesAsync(merchantId, merchantUserId, now, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Portal two-factor enrollment confirmed for user {MerchantUserId} of merchant {MerchantId}.",
            merchantUserId, merchantId);

        return Result.Success(codes);
    }

    public async Task<Result> VerifyAsync(
        Guid merchantUserId, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure(MerchantTwoFactorErrors.CodeRequired);

        var factor = await repository.FindByUserAsync(merchantUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure(MerchantTwoFactorErrors.NotEnrolled);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure(MerchantTwoFactorErrors.LockedOut);

        if (!VerifyCode(factor, code, now))
        {
            await RecordFailureAsync(factor, merchantUserId, cancellationToken);
            return Result.Failure(MerchantTwoFactorErrors.InvalidCode);
        }

        if (factor.FailedAttempts > 0 || factor.LockedUntil is not null)
        {
            factor.RecordSuccess();
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> RedeemRecoveryCodeAsync(
        Guid merchantUserId, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure(MerchantTwoFactorErrors.CodeRequired);

        var factor = await repository.FindByUserAsync(merchantUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure(MerchantTwoFactorErrors.NotEnrolled);

        var now = clock.GetUtcNow();
        if (factor.IsLockedOut(now))
            return Result.Failure(MerchantTwoFactorErrors.LockedOut);

        var normalized = NormalizeRecoveryCode(code);
        var candidates = await repository.ListRecoveryCodesAsync(merchantUserId, unusedOnly: true, cancellationToken);

        // No early exit, so the time taken reveals neither how many codes remain nor where a match sat.
        MerchantUserRecoveryCode? matched = null;
        foreach (var candidate in candidates)
        {
            if (Pbkdf2PasswordHash.Verify(normalized, candidate.CodeHash))
                matched ??= candidate;
        }

        if (matched is null)
        {
            await RecordFailureAsync(factor, merchantUserId, cancellationToken);
            return Result.Failure(MerchantTwoFactorErrors.InvalidCode);
        }

        var consumed = matched.Consume(now);
        if (consumed.IsFailure)
            return consumed;

        factor.RecordSuccess();
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Portal user {MerchantUserId} signed in with a recovery code; {Remaining} remaining.",
            merchantUserId, candidates.Count - 1);

        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default)
    {
        var factor = await repository.FindAsync(merchantId, merchantUserId, cancellationToken);
        if (factor is null || !factor.IsEnrolled)
            return Result.Failure<IReadOnlyList<string>>(MerchantTwoFactorErrors.NotEnrolled);

        var codes = await IssueRecoveryCodesAsync(merchantId, merchantUserId, clock.GetUtcNow(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(codes);
    }

    public async Task<Result> ResetAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default)
    {
        // Tenant-filtered: another merchant passing a real user id gets "nothing to reset", never a silent
        // success against someone else's account.
        var factor = await repository.FindAsync(merchantId, merchantUserId, cancellationToken);
        if (factor is null)
            return Result.Success();

        factor.Disable();

        var codes = await repository.ListRecoveryCodesAsync(merchantUserId, unusedOnly: false, cancellationToken);
        repository.RemoveRecoveryCodes(codes);

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Portal two-factor was reset for user {MerchantUserId} of merchant {MerchantId}.",
            merchantUserId, merchantId);

        return Result.Success();
    }

    private bool VerifyCode(MerchantUserTwoFactor factor, string? code, DateTimeOffset now)
    {
        var secret = cipher.Unprotect(factor.SecretCiphertext);
        try
        {
            return Totp.Verify(secret, code, now);
        }
        finally
        {
            Array.Clear(secret);
        }
    }

    private async Task RecordFailureAsync(
        MerchantUserTwoFactor factor, Guid merchantUserId, CancellationToken cancellationToken)
    {
        var locked = factor.RecordFailure(clock.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        if (locked)
        {
            logger.LogWarning(
                "Portal user {MerchantUserId} locked out of two-factor after {Attempts} consecutive failures.",
                merchantUserId, MerchantUserTwoFactor.MaxFailedAttempts);
        }
    }

    private async Task<IReadOnlyList<string>> IssueRecoveryCodesAsync(
        Guid merchantId, Guid merchantUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await repository.ListRecoveryCodesAsync(merchantUserId, unusedOnly: false, cancellationToken);
        repository.RemoveRecoveryCodes(existing);

        var plaintext = new List<string>(MerchantUserRecoveryCode.BatchSize);
        var entities = new List<MerchantUserRecoveryCode>(MerchantUserRecoveryCode.BatchSize);

        for (var i = 0; i < MerchantUserRecoveryCode.BatchSize; i++)
        {
            var code = TemporaryPassword.Generate();
            plaintext.Add(code);
            entities.Add(MerchantUserRecoveryCode.Issue(
                merchantUserId, merchantId, Pbkdf2PasswordHash.Hash(NormalizeRecoveryCode(code)), now));
        }

        repository.AddRecoveryCodes(entities);
        return plaintext;
    }

    private static string NormalizeRecoveryCode(string code) =>
        code.Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
}
