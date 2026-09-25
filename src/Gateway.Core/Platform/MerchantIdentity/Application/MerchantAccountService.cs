using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed record MerchantAccountView(
    Guid MerchantUserId, string Username, string DisplayName, Guid? RoleId, string? RoleName, string Status,
    bool MustChangePassword, DateTimeOffset CreatedAt,
    /// <summary>The merchant's original super-admin — see <c>MerchantUser.IsPrimary</c>. Platform staff may
    /// only reset this one account's password; every other account is the merchant's own business, managed
    /// inside its own portal.</summary>
    bool IsPrimary = false,
    /// <summary>The live 2FA switch (§ MerchantUser.RequireTwoFactor) — independent of whether the account
    /// has actually bound an authenticator.</summary>
    bool RequireTwoFactor = true);

/// <summary>The generated one-time password, readable exactly once — at creation or reset. Never stored
/// recoverably (only its PBKDF2 hash is), never logged.</summary>
public sealed record MerchantAccountCredential(Guid MerchantUserId, string Username, string TemporaryPassword);

/// <summary>
/// Per-tenant account management for the merchant portal. Like <see cref="IMerchantRoleService"/>, every method
/// takes the caller's <c>merchantId</c> and scopes every lookup by it, so an account id from another tenant
/// reads as "not found" rather than being actionable.
/// </summary>
public interface IMerchantAccountService
{
    Task<Result<MerchantAccountCredential>> CreateAsync(
        Guid merchantId, string username, string displayName, Guid? roleId, bool requireTwoFactor,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MerchantAccountView>>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>The merchant's single primary/super-admin account — the ONE account platform staff may act on
    /// via <see cref="ResetPrimaryPasswordAsync"/>. Null only if the merchant somehow has no accounts at all
    /// (never provisioned, or every account predates the primary concept and the backfill missed it).</summary>
    Task<Result<MerchantAccountView?>> GetPrimaryAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<Result> SetStatusAsync(
        Guid merchantId, Guid targetUserId, Guid actingUserId, bool active, CancellationToken cancellationToken = default);

    Task<Result> AssignRoleAsync(Guid merchantId, Guid targetUserId, Guid? roleId, CancellationToken cancellationToken = default);

    Task<Result<MerchantAccountCredential>> ResetPasswordAsync(
        Guid merchantId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>The platform-staff reset path — refuses (<see cref="MerchantUserErrors.OnlyPrimaryResettableByStaff"/>)
    /// unless <paramref name="targetUserId"/> is the merchant's primary account, then defers to
    /// <see cref="ResetPasswordAsync"/>. Deliberately a SEPARATE method from the plain reset above rather than a
    /// flag on it: <see cref="ResetPasswordAsync"/> also serves the merchant's own admin resetting a teammate
    /// inside the portal, which must stay unrestricted — only the platform-staff path is primary-only.</summary>
    Task<Result<MerchantAccountCredential>> ResetPrimaryPasswordAsync(
        Guid merchantId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user changing their OWN password — requires the current one, and clears the
    /// forced-change flag. Deliberately not an admin action: an admin resets (issuing a one-time password),
    /// never sets a chosen password for someone else.</summary>
    Task<Result> ChangeOwnPasswordAsync(
        Guid merchantId, Guid actingUserId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Flips the live 2FA switch (§ MerchantUser.RequireTwoFactor). Refuses a self-target
    /// (<see cref="MerchantUserErrors.CannotChangeOwnTwoFactorRequirement"/>) — same lock-out class as
    /// <see cref="SetStatusAsync"/>: doing it to yourself would let you drop out of 2FA with nobody else's
    /// sign-off. Applies to the primary account too — it is not exempt from this switch, only from disable.</summary>
    Task<Result> SetRequireTwoFactorAsync(
        Guid merchantId, Guid targetUserId, Guid actingUserId, bool requireTwoFactor,
        CancellationToken cancellationToken = default);
}

public sealed class MerchantAccountService(
    IMerchantUserRepository users,
    IMerchantRoleRepository roles,
    IMerchantPasswordHasher hasher,
    IMerchantPasswordGenerator passwordGenerator,
    TimeProvider timeProvider) : IMerchantAccountService
{
    private const int MinimumPasswordLength = 12;

    public async Task<Result<MerchantAccountCredential>> CreateAsync(
        Guid merchantId, string username, string displayName, Guid? roleId, bool requireTwoFactor,
        CancellationToken cancellationToken = default)
    {
        var normalised = username.Trim();
        if (await users.UsernameExistsAsync(normalised, cancellationToken))
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.UsernameAlreadyExists);

        // A role from another tenant must never be assignable — verified against THIS merchant's roles.
        if (roleId is { } id && await roles.FindByIdAsync(merchantId, id, cancellationToken) is null)
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.RoleNotInTenant);

        // The merchant's first-ever account (any status) becomes its permanent primary/super-admin — see
        // MerchantUser.IsPrimary. The filtered unique index (MerchantId WHERE IsPrimary = 1) is the real
        // race-safety arbiter; this check just makes the common case correct without any caller needing to
        // know or care about it.
        var isPrimary = (await users.ListAsync(merchantId, cancellationToken)).Count == 0;

        var temporaryPassword = passwordGenerator.Generate();
        var user = MerchantUser.Create(
            merchantId, normalised, displayName, hasher.Hash(temporaryPassword), roleId,
            mustChangePassword: true, isPrimary, requireTwoFactor, timeProvider.GetUtcNow());
        if (user.IsFailure)
            return Result.Failure<MerchantAccountCredential>(user.Error!);

        users.Add(user.Value);
        await users.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantAccountCredential(user.Value.Id, user.Value.Username, temporaryPassword));
    }

    public async Task<Result<IReadOnlyList<MerchantAccountView>>> ListAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var accounts = await users.ListAsync(merchantId, cancellationToken);
        var roleNames = (await roles.ListAsync(merchantId, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);

        IReadOnlyList<MerchantAccountView> views = accounts
            .Select(u => ToView(u, roleNames))
            .ToList();

        return Result.Success(views);
    }

    public async Task<Result<MerchantAccountView?>> GetPrimaryAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var accounts = await users.ListAsync(merchantId, cancellationToken);
        var primary = accounts.FirstOrDefault(u => u.IsPrimary);
        if (primary is null)
            return Result.Success<MerchantAccountView?>(null);

        var roleNames = (await roles.ListAsync(merchantId, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);
        return Result.Success<MerchantAccountView?>(ToView(primary, roleNames));
    }

    private static MerchantAccountView ToView(MerchantUser u, IReadOnlyDictionary<Guid, string> roleNames) =>
        new(u.Id, u.Username, u.DisplayName, u.RoleId,
            u.RoleId is { } rid ? roleNames.GetValueOrDefault(rid) : null,
            u.Status.ToString(), u.MustChangePassword, u.CreatedAt, u.IsPrimary, u.RequireTwoFactor);

    public async Task<Result> SetStatusAsync(
        Guid merchantId, Guid targetUserId, Guid actingUserId, bool active, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(merchantId, targetUserId, cancellationToken);
        if (user is null)
            return Result.Failure(MerchantUserErrors.NotFound);

        if (!active)
        {
            // Two lock-out guards, same as the staff module: never disable yourself, and never remove the
            // tenant's last active account — either would leave the merchant unable to sign in at all.
            if (targetUserId == actingUserId)
                return Result.Failure(MerchantUserErrors.CannotDisableSelf);

            if (user.Status == MerchantUserStatus.Active && await users.CountActiveAsync(merchantId, cancellationToken) <= 1)
                return Result.Failure(MerchantUserErrors.CannotDisableLastActiveAccount);
        }

        var statusResult = user.SetStatus(active ? MerchantUserStatus.Active : MerchantUserStatus.Disabled);
        if (statusResult.IsFailure)
            return statusResult;

        await users.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> AssignRoleAsync(
        Guid merchantId, Guid targetUserId, Guid? roleId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(merchantId, targetUserId, cancellationToken);
        if (user is null)
            return Result.Failure(MerchantUserErrors.NotFound);

        if (roleId is { } id && await roles.FindByIdAsync(merchantId, id, cancellationToken) is null)
            return Result.Failure(MerchantUserErrors.RoleNotInTenant);

        user.AssignRole(roleId);
        await users.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<MerchantAccountCredential>> ResetPasswordAsync(
        Guid merchantId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(merchantId, targetUserId, cancellationToken);
        if (user is null)
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.NotFound);

        var temporaryPassword = passwordGenerator.Generate();
        var result = user.ResetPassword(hasher.Hash(temporaryPassword));
        if (result.IsFailure)
            return Result.Failure<MerchantAccountCredential>(result.Error!);

        await users.SaveChangesAsync(cancellationToken);
        return Result.Success(new MerchantAccountCredential(user.Id, user.Username, temporaryPassword));
    }

    public async Task<Result<MerchantAccountCredential>> ResetPrimaryPasswordAsync(
        Guid merchantId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(merchantId, targetUserId, cancellationToken);
        if (user is null)
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.NotFound);

        if (!user.IsPrimary)
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.OnlyPrimaryResettableByStaff);

        return await ResetPasswordAsync(merchantId, targetUserId, cancellationToken);
    }

    public async Task<Result> SetRequireTwoFactorAsync(
        Guid merchantId, Guid targetUserId, Guid actingUserId, bool requireTwoFactor,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId == actingUserId)
            return Result.Failure(MerchantUserErrors.CannotChangeOwnTwoFactorRequirement);

        var user = await users.FindByIdAsync(merchantId, targetUserId, cancellationToken);
        if (user is null)
            return Result.Failure(MerchantUserErrors.NotFound);

        user.SetRequireTwoFactor(requireTwoFactor);
        await users.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ChangeOwnPasswordAsync(
        Guid merchantId, Guid actingUserId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(merchantId, actingUserId, cancellationToken);
        if (user is null)
            return Result.Failure(MerchantUserErrors.NotFound);

        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return Result.Failure(MerchantUserErrors.CurrentPasswordIncorrect);

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinimumPasswordLength)
            return Result.Failure(MerchantUserErrors.PasswordTooShort);

        var result = user.ChangeOwnPassword(hasher.Hash(newPassword));
        if (result.IsFailure)
            return result;

        await users.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
