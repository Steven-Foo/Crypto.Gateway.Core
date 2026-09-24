using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Tests;

/// <summary>
/// The portal's second factor. The tests that matter most here are the TENANT ones: this module's whole
/// discipline is that a merchant cannot reach another merchant's data even holding an exact id, and a second
/// factor is exactly the thing an attacker would want to clear on someone else's account.
/// </summary>
public sealed class MerchantTwoFactorServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantIdentityTwoFactorTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantIdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<MerchantIdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static AesGcmMerchantTwoFactorSecretCipher Cipher() =>
        new(Options.Create(new MerchantTwoFactorSecretOptions
        {
            CurrentKeyVersion = 1,
            Keys = { [1] = Convert.ToBase64String(Enumerable.Repeat((byte)0x3B, 32).ToArray()) },
        }));

    private static MerchantTwoFactorService Service(MerchantIdentityDbContext context, TimeProvider? clock = null) =>
        new(new MerchantTwoFactorRepository(context), Cipher(),
            Options.Create(new MerchantIdentityOptions()),
            clock ?? TimeProvider.System, NullLogger<MerchantTwoFactorService>.Instance);

    private readonly Guid _merchantA = Guid.CreateVersion7();
    private readonly Guid _merchantB = Guid.CreateVersion7();
    private readonly Guid _userA = Guid.CreateVersion7();

    private async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes)> EnrollAsync(
        Guid merchantId, Guid userId)
    {
        await using var context = Context();
        var service = Service(context);

        var enrollment = await service.BeginEnrollmentAsync(merchantId, userId, "merchant001", Ct);
        enrollment.IsSuccess.ShouldBeTrue();

        Totp.TryFromBase32(enrollment.Value.SecretBase32, out var secret).ShouldBeTrue();

        var confirm = await service.ConfirmEnrollmentAsync(
            merchantId, userId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);
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

    [Fact]
    public async Task Enrollment_activates_the_factor_and_issues_recovery_codes()
    {
        var (_, codes) = await EnrollAsync(_merchantA, _userA);

        codes.Count.ShouldBe(MerchantUserRecoveryCode.BatchSize);

        await using var context = Context();
        var status = await Service(context).GetStatusAsync(_merchantA, _userA, Ct);

        status.Enrolled.ShouldBeTrue();
        status.Status.ShouldBe(MerchantTwoFactorStatus.Active);
        status.RecoveryCodesRemaining.ShouldBe(MerchantUserRecoveryCode.BatchSize);
    }

    [Fact]
    public async Task A_pending_enrollment_grants_nothing()
    {
        await using var context = Context();
        var service = Service(context);

        await service.BeginEnrollmentAsync(_merchantA, _userA, "merchant001", Ct);

        (await service.IsEnrolledAsync(_userA, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_secret_is_stored_encrypted_and_recovers_intact()
    {
        await using var context = Context();
        var enrollment = await Service(context).BeginEnrollmentAsync(_merchantA, _userA, "merchant001", Ct);

        await using var verify = Context();
        var stored = await verify.MerchantUserTwoFactors.SingleAsync(f => f.MerchantUserId == _userA, Ct);

        stored.SecretCiphertext.ShouldNotContain(enrollment.Value.SecretBase32);
        Totp.ToBase32(Cipher().Unprotect(stored.SecretCiphertext)).ShouldBe(enrollment.Value.SecretBase32);
    }

    [Fact]
    public async Task A_valid_code_verifies_and_is_not_consumed()
    {
        var (secret, _) = await EnrollAsync(_merchantA, _userA);
        var code = Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow));

        await using var context = Context();
        var service = Service(context);

        // Codes are verified, never consumed (docs/two-factor-authentication.md §6.1).
        (await service.VerifyAsync(_userA, code, Ct)).IsSuccess.ShouldBeTrue();
        (await service.VerifyAsync(_userA, code, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_retry_works_immediately()
    {
        var (secret, _) = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var service = Service(context);

        (await service.VerifyAsync(_userA, "000000", Ct)).Error!.Code
            .ShouldBe(MerchantTwoFactorErrors.InvalidCode.Code);

        (await service.VerifyAsync(_userA, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Repeated_failures_lock_the_factor_and_the_lock_expires()
    {
        var (secret, _) = await EnrollAsync(_merchantA, _userA);
        var clock = new PortalFakeClock(DateTimeOffset.UtcNow);

        await using var context = Context();
        var service = Service(context, clock);

        for (var i = 0; i < MerchantUserTwoFactor.MaxFailedAttempts; i++)
            await service.VerifyAsync(_userA, "000000", Ct);

        (await service.VerifyAsync(_userA, Totp.ComputeAt(secret, Totp.StepAt(clock.GetUtcNow())), Ct))
            .Error!.Code.ShouldBe(MerchantTwoFactorErrors.LockedOut.Code);

        clock.Advance(MerchantUserTwoFactor.LockoutDuration + TimeSpan.FromMinutes(1));

        (await service.VerifyAsync(_userA, Totp.ComputeAt(secret, Totp.StepAt(clock.GetUtcNow())), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        var (_, codes) = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var service = Service(context);

        (await service.RedeemRecoveryCodeAsync(_userA, codes[0], Ct)).IsSuccess.ShouldBeTrue();
        (await service.RedeemRecoveryCodeAsync(_userA, codes[0], Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Re_enrolling_while_active_is_refused()
    {
        _ = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var again = await Service(context).BeginEnrollmentAsync(_merchantA, _userA, "merchant001", Ct);

        again.Error!.Code.ShouldBe(MerchantTwoFactorErrors.AlreadyEnrolled.Code);
    }

    // ── tenant isolation: the tests this module exists to keep honest ──

    /// <summary>
    /// Merchant B must not be able to clear merchant A's user's second factor, even knowing the exact user
    /// id. If this ever passes silently, one merchant can strip another's account protection and the whole
    /// tenant boundary is decorative.
    /// </summary>
    [Fact]
    public async Task A_merchant_cannot_reset_another_merchants_users_factor()
    {
        var (secret, _) = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var service = Service(context);

        // Reported as a no-op success (nothing of B's to reset) rather than as an error, so B learns nothing
        // about whether that id exists at all.
        (await service.ResetAsync(_merchantB, _userA, Ct)).IsSuccess.ShouldBeTrue();

        // What matters: A's factor is untouched and still works.
        (await service.IsEnrolledAsync(_userA, Ct)).ShouldBeTrue();
        (await service.VerifyAsync(_userA, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_merchant_cannot_read_another_merchants_users_status()
    {
        _ = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var status = await Service(context).GetStatusAsync(_merchantB, _userA, Ct);

        // Reads as "no factor", the same answer an unknown id gives — never a confirmation that the account
        // exists and is enrolled.
        status.Enrolled.ShouldBeFalse();
        status.Status.ShouldBeNull();
    }

    [Fact]
    public async Task A_merchant_cannot_regenerate_another_merchants_users_recovery_codes()
    {
        var (_, codes) = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var service = Service(context);

        (await service.RegenerateRecoveryCodesAsync(_merchantB, _userA, Ct)).IsFailure.ShouldBeTrue();

        // A's printed codes still work — the failed cross-tenant call invalidated nothing.
        (await service.RedeemRecoveryCodeAsync(_userA, codes[0], Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Resetting_unenrolls_and_destroys_the_recovery_codes()
    {
        var (_, codes) = await EnrollAsync(_merchantA, _userA);

        await using var context = Context();
        var service = Service(context);

        (await service.ResetAsync(_merchantA, _userA, Ct)).IsSuccess.ShouldBeTrue();

        (await service.IsEnrolledAsync(_userA, Ct)).ShouldBeFalse();
        // A reset that removes the authenticator but keeps ten printed ways in is not a reset.
        (await service.RedeemRecoveryCodeAsync(_userA, codes[0], Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Two_users_of_different_merchants_enroll_independently()
    {
        var userB = Guid.CreateVersion7();

        var (secretA, _) = await EnrollAsync(_merchantA, _userA);
        var (secretB, _) = await EnrollAsync(_merchantB, userB);

        await using var context = Context();
        var service = Service(context);

        // Each code works only for its own account — the unique index is per user, not per tenant.
        (await service.VerifyAsync(_userA, Totp.ComputeAt(secretA, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();
        (await service.VerifyAsync(userB, Totp.ComputeAt(secretA, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsFailure.ShouldBeTrue();
        (await service.VerifyAsync(userB, Totp.ComputeAt(secretB, Totp.StepAt(DateTimeOffset.UtcNow)), Ct))
            .IsSuccess.ShouldBeTrue();
    }
}

internal sealed class PortalFakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
