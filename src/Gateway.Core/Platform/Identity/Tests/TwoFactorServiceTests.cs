using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

/// <summary>
/// Against real SQL Server, like the rest of this module — the unique index on <c>StaffUserId</c> and the
/// recovery-code deletion behaviour are database facts, and an in-memory provider would assert neither.
/// </summary>
public sealed class TwoFactorServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityTwoFactorTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>A throwaway AES-256 key. Real deployments read this from configuration (§10).</summary>
    private static AesGcmTwoFactorSecretCipher Cipher() =>
        new(Options.Create(new TwoFactorSecretOptions
        {
            CurrentKeyVersion = 1,
            Keys = { [1] = Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 32).ToArray()) },
        }));

    private static TwoFactorService Service(IdentityDbContext context, TimeProvider? clock = null) =>
        new(new StaffTwoFactorRepository(context), Cipher(),
            Options.Create(new TwoFactorOptions { Issuer = "CryptoPaymentEngine" }),
            clock ?? TimeProvider.System, NullLogger<TwoFactorService>.Instance);

    private readonly Guid _staffUserId = Guid.CreateVersion7();

    /// <summary>Enrolls the account, returning its secret (so a test can produce real codes) and the
    /// recovery codes issued on confirmation.</summary>
    private async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes)> EnrollAsync(Guid? staffUserId = null)
    {
        var id = staffUserId ?? _staffUserId;

        await using var context = Context();
        var service = Service(context);

        var enrollment = await service.BeginEnrollmentAsync(id, "admin1", Ct);
        enrollment.IsSuccess.ShouldBeTrue();

        Totp.TryFromBase32(enrollment.Value.SecretBase32, out var secret).ShouldBeTrue();

        var confirm = await service.ConfirmEnrollmentAsync(
            id, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);
        confirm.IsSuccess.ShouldBeTrue();

        return (secret, confirm.Value);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    // ── enrollment ──

    [Fact]
    public async Task Beginning_enrollment_returns_a_scannable_uri_and_leaves_the_account_unenrolled()
    {
        await using var context = Context();
        var service = Service(context);

        var enrollment = await service.BeginEnrollmentAsync(_staffUserId, "admin1", Ct);

        enrollment.IsSuccess.ShouldBeTrue();
        enrollment.Value.ProvisioningUri.ShouldStartWith("otpauth://totp/");
        enrollment.Value.ProvisioningUri.ShouldContain("issuer=CryptoPaymentEngine");
        enrollment.Value.SecretBase32.ShouldNotBeNullOrWhiteSpace();

        // Pending grants NOTHING. Without this, an interrupted enrollment leaves an account that believes it
        // has 2FA and an operator who cannot produce a code for it.
        (await service.IsEnrolledAsync(_staffUserId, Ct)).ShouldBeFalse();

        var status = await service.GetStatusAsync(_staffUserId, Ct);
        status.Enrolled.ShouldBeFalse();
        status.Status.ShouldBe(TwoFactorStatus.Pending);
        status.RecoveryCodesRemaining.ShouldBe(0);
    }

    [Fact]
    public async Task The_secret_is_never_stored_in_plaintext()
    {
        await using var context = Context();
        var enrollment = await Service(context).BeginEnrollmentAsync(_staffUserId, "admin1", Ct);

        await using var verify = Context();
        var stored = await verify.StaffTwoFactors.SingleAsync(f => f.StaffUserId == _staffUserId, Ct);

        stored.SecretCiphertext.ShouldNotContain(enrollment.Value.SecretBase32);
        // ...and it is genuinely recoverable, not merely scrambled: the round trip must give back the secret
        // the authenticator was provisioned with, or no code would ever verify.
        Totp.ToBase32(Cipher().Unprotect(stored.SecretCiphertext)).ShouldBe(enrollment.Value.SecretBase32);
    }

    [Fact]
    public async Task Confirming_with_a_valid_code_activates_the_factor_and_issues_recovery_codes()
    {
        _ = await EnrollAsync();

        await using var context = Context();
        var service = Service(context);

        (await service.IsEnrolledAsync(_staffUserId, Ct)).ShouldBeTrue();

        var status = await service.GetStatusAsync(_staffUserId, Ct);
        status.Status.ShouldBe(TwoFactorStatus.Active);
        status.EnrolledAt.ShouldNotBeNull();
        status.RecoveryCodesRemaining.ShouldBe(StaffRecoveryCode.BatchSize);
    }

    [Fact]
    public async Task Confirming_with_a_wrong_code_does_not_activate_the_factor()
    {
        await using var context = Context();
        var service = Service(context);
        await service.BeginEnrollmentAsync(_staffUserId, "admin1", Ct);

        var confirm = await service.ConfirmEnrollmentAsync(_staffUserId, "000000", Ct);

        confirm.IsFailure.ShouldBeTrue();
        confirm.Error!.Code.ShouldBe(TwoFactorErrors.InvalidCode.Code);
        (await service.IsEnrolledAsync(_staffUserId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Confirming_without_starting_is_refused()
    {
        await using var context = Context();

        var confirm = await Service(context).ConfirmEnrollmentAsync(_staffUserId, "123456", Ct);

        confirm.Error!.Code.ShouldBe(TwoFactorErrors.EnrollmentNotStarted.Code);
    }

    [Fact]
    public async Task Enrollment_can_be_restarted_while_unfinished()
    {
        await using var context = Context();
        var service = Service(context);

        var first = await service.BeginEnrollmentAsync(_staffUserId, "admin1", Ct);
        var second = await service.BeginEnrollmentAsync(_staffUserId, "admin1", Ct);

        // Someone closed the page, or scanned onto a device they then wiped. A fresh secret, and the old one
        // stops working.
        second.IsSuccess.ShouldBeTrue();
        second.Value.SecretBase32.ShouldNotBe(first.Value.SecretBase32);

        Totp.TryFromBase32(first.Value.SecretBase32, out var oldSecret);
        var stale = await service.ConfirmEnrollmentAsync(
            _staffUserId, Totp.ComputeAt(oldSecret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);
        stale.IsFailure.ShouldBeTrue();
    }

    /// <summary>
    /// The account-takeover path this closes: anyone holding a live session could otherwise silently move
    /// the second factor onto their own phone. Recovery goes through a recovery code or an admin reset,
    /// both of which are recorded.
    /// </summary>
    [Fact]
    public async Task Re_enrolling_while_already_active_is_refused()
    {
        _ = await EnrollAsync();

        await using var context = Context();
        var again = await Service(context).BeginEnrollmentAsync(_staffUserId, "admin1", Ct);

        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe(TwoFactorErrors.AlreadyEnrolled.Code);
    }

    // ── verification ──

    [Fact]
    public async Task A_valid_code_verifies()
    {
        var (secret, _) = await EnrollAsync();

        await using var context = Context();
        var result = await Service(context).VerifyAsync(
            _staffUserId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// The decision recorded in docs §6.1: codes are verified, never consumed. An operator working through a
    /// settlement queue must not be paced at one action per thirty-second step. This pins the behaviour so
    /// "tightening" it becomes a deliberate decision rather than an accident.
    /// </summary>
    [Fact]
    public async Task The_same_code_can_be_used_for_several_actions_in_its_window()
    {
        var (secret, _) = await EnrollAsync();
        var code = Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow));

        await using var context = Context();
        var service = Service(context);

        (await service.VerifyAsync(_staffUserId, code, Ct)).IsSuccess.ShouldBeTrue();
        (await service.VerifyAsync(_staffUserId, code, Ct)).IsSuccess.ShouldBeTrue();
        (await service.VerifyAsync(_staffUserId, code, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_a_correct_one_still_works_immediately_after()
    {
        var (secret, _) = await EnrollAsync();

        await using var context = Context();
        var service = Service(context);

        var wrong = await service.VerifyAsync(_staffUserId, "000000", Ct);
        wrong.IsFailure.ShouldBeTrue();
        wrong.Error!.Code.ShouldBe(TwoFactorErrors.InvalidCode.Code);

        // Retry is the designed behaviour: a refusal must leave the account immediately usable.
        var right = await service.VerifyAsync(
            _staffUserId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);
        right.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unenrolled_account_is_refused_rather_than_waved_through()
    {
        await using var context = Context();

        // Fail closed. The dangerous version of this bug passes an account with no factor at all.
        var result = await Service(context).VerifyAsync(Guid.CreateVersion7(), "123456", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(TwoFactorErrors.NotEnrolled.Code);
    }

    [Fact]
    public async Task A_missing_code_is_refused()
    {
        _ = await EnrollAsync();

        await using var context = Context();
        var result = await Service(context).VerifyAsync(_staffUserId, "  ", Ct);

        result.Error!.Code.ShouldBe(TwoFactorErrors.CodeRequired.Code);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_factor_and_the_lock_expires()
    {
        var (secret, _) = await EnrollAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        await using var context = Context();
        var service = Service(context, clock);

        for (var i = 0; i < StaffTwoFactor.MaxFailedAttempts; i++)
            (await service.VerifyAsync(_staffUserId, "000000", Ct)).IsFailure.ShouldBeTrue();

        // A correct code is now refused too — the lock is on the factor, not on the guess.
        var locked = await service.VerifyAsync(
            _staffUserId, Totp.ComputeAt(secret, Totp.StepAt(clock.GetUtcNow())), Ct);
        locked.Error!.Code.ShouldBe(TwoFactorErrors.LockedOut.Code);

        clock.Advance(StaffTwoFactor.LockoutDuration + TimeSpan.FromMinutes(1));

        var after = await service.VerifyAsync(
            _staffUserId, Totp.ComputeAt(secret, Totp.StepAt(clock.GetUtcNow())), Ct);
        after.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_success_clears_the_failure_streak()
    {
        var (secret, _) = await EnrollAsync();

        await using var context = Context();
        var service = Service(context);

        for (var i = 0; i < StaffTwoFactor.MaxFailedAttempts - 1; i++)
            await service.VerifyAsync(_staffUserId, "000000", Ct);

        (await service.VerifyAsync(_staffUserId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();

        // One more miss must not tip a cleared streak straight into a lockout.
        (await service.VerifyAsync(_staffUserId, "000000", Ct)).Error!.Code
            .ShouldBe(TwoFactorErrors.InvalidCode.Code);
    }

    // ── recovery codes ──

    [Fact]
    public async Task A_recovery_code_works_once_and_only_once()
    {
        var (_, codes) = await EnrollAsync();
        codes.Count.ShouldBe(StaffRecoveryCode.BatchSize);

        await using var context = Context();
        var service = Service(context);

        (await service.RedeemRecoveryCodeAsync(_staffUserId, codes[0], Ct)).IsSuccess.ShouldBeTrue();
        (await service.RedeemRecoveryCodeAsync(_staffUserId, codes[0], Ct)).IsFailure.ShouldBeTrue();

        (await service.GetStatusAsync(_staffUserId, Ct)).RecoveryCodesRemaining
            .ShouldBe(StaffRecoveryCode.BatchSize - 1);
    }

    [Fact]
    public async Task Regenerating_invalidates_the_previous_batch()
    {
        _ = await EnrollAsync();

        await using var context = Context();
        var service = Service(context);

        var first = (await service.RegenerateRecoveryCodesAsync(_staffUserId, Ct)).Value;
        var second = (await service.RegenerateRecoveryCodesAsync(_staffUserId, Ct)).Value;

        // The old list is printed on paper somewhere; leaving it live alongside a replacement would quietly
        // double the number of valid ways in.
        (await service.RedeemRecoveryCodeAsync(_staffUserId, first[0], Ct)).IsFailure.ShouldBeTrue();
        (await service.RedeemRecoveryCodeAsync(_staffUserId, second[0], Ct)).IsSuccess.ShouldBeTrue();
        (await service.GetStatusAsync(_staffUserId, Ct)).RecoveryCodesRemaining
            .ShouldBe(StaffRecoveryCode.BatchSize - 1);
    }

    [Fact]
    public async Task An_unknown_recovery_code_is_refused()
    {
        _ = await EnrollAsync();

        await using var context = Context();
        var result = await Service(context).RedeemRecoveryCodeAsync(_staffUserId, "NOTAREALCODE", Ct);

        result.IsFailure.ShouldBeTrue();
    }

    // ── reset ──

    [Fact]
    public async Task Resetting_unenrolls_the_account_and_destroys_its_recovery_codes()
    {
        _ = await EnrollAsync();

        await using var setup = Context();
        var codes = (await Service(setup).RegenerateRecoveryCodesAsync(_staffUserId, Ct)).Value;

        await using var context = Context();
        var service = Service(context);

        (await service.ResetAsync(_staffUserId, Ct)).IsSuccess.ShouldBeTrue();

        (await service.IsEnrolledAsync(_staffUserId, Ct)).ShouldBeFalse();

        // A reset that removes the authenticator but silently keeps ten printed ways in is not a reset.
        (await service.RedeemRecoveryCodeAsync(_staffUserId, codes[0], Ct)).IsFailure.ShouldBeTrue();

        await using var verify = Context();
        (await verify.StaffRecoveryCodes.CountAsync(c => c.StaffUserId == _staffUserId, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Resetting_an_unenrolled_account_is_not_an_error()
    {
        await using var context = Context();

        (await Service(context).ResetAsync(Guid.CreateVersion7(), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_can_enroll_again_after_a_reset()
    {
        _ = await EnrollAsync();

        await using var reset = Context();
        await Service(reset).ResetAsync(_staffUserId, Ct);

        var (secret, _) = await EnrollAsync();

        await using var context = Context();
        (await Service(context).VerifyAsync(
            _staffUserId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();
    }

}

/// <summary>A clock a test can move, for lockout expiry.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
