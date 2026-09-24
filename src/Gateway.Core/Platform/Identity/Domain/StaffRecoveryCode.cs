using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// One single-use fallback code, issued in a batch when a staff member completes enrollment and displayed
/// exactly once.
///
/// <para>Stored as a PBKDF2 hash, like a password and unlike the TOTP secret: a recovery code is a credential
/// the server only ever needs to <em>check</em>, never to reproduce, so there is no reason to hold anything
/// reversible. (<see cref="Pbkdf2PasswordHash"/> rather than a fast hash because these are short enough to be
/// typed by a human.)</para>
///
/// <para><b>A recovery code satisfies login. It does NOT satisfy a guarded action.</b> These exist so a lost
/// phone does not lock someone out of the back office — they are not a factor a person deliberately carries
/// at the moment they move money. If one could clear a guarded action, the whole control would degrade to
/// "whoever holds the printout". Someone who has lost their authenticator signs in with a recovery code and
/// their first act is to enroll again.</para>
/// </summary>
public sealed class StaffRecoveryCode : Entity<Guid>
{
    /// <summary>How many are issued per batch. Enough to survive a few uses before anyone reissues, few
    /// enough that the printed list stays something a person actually keeps somewhere safe.</summary>
    public const int BatchSize = 10;

    private StaffRecoveryCode(Guid id, Guid staffUserId, string codeHash, DateTimeOffset now) : base(id)
    {
        StaffUserId = staffUserId;
        CodeHash = codeHash;
        CreatedAt = now;
    }

    private StaffRecoveryCode() : base(Guid.Empty)
    {
    }

    public Guid StaffUserId { get; private set; }
    public string CodeHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsUsable => UsedAt is null;

    public static StaffRecoveryCode Issue(Guid staffUserId, string codeHash, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), staffUserId, codeHash, now);

    /// <summary>Marks the code spent. Returns a failure if it was already used, so a replayed code is an
    /// explicit refusal rather than a silent second success.</summary>
    public Result Consume(DateTimeOffset now)
    {
        if (UsedAt is not null)
            return Result.Failure(TwoFactorErrors.RecoveryCodeAlreadyUsed);

        UsedAt = now;
        return Result.Success();
    }
}
