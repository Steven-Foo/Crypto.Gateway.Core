using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed record MerchantAccountView(
    Guid MerchantUserId, string Username, string DisplayName, Guid? RoleId, string? RoleName, string Status,
    bool MustChangePassword, DateTimeOffset CreatedAt);

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
        Guid merchantId, string username, string displayName, Guid? roleId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MerchantAccountView>>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<Result> SetStatusAsync(
        Guid merchantId, Guid targetUserId, Guid actingUserId, bool active, CancellationToken cancellationToken = default);

    Task<Result> AssignRoleAsync(Guid merchantId, Guid targetUserId, Guid? roleId, CancellationToken cancellationToken = default);

    Task<Result<MerchantAccountCredential>> ResetPasswordAsync(
        Guid merchantId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user changing their OWN password — requires the current one, and clears the
    /// forced-change flag. Deliberately not an admin action: an admin resets (issuing a one-time password),
    /// never sets a chosen password for someone else.</summary>
    Task<Result> ChangeOwnPasswordAsync(
        Guid merchantId, Guid actingUserId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
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
        Guid merchantId, string username, string displayName, Guid? roleId, CancellationToken cancellationToken = default)
    {
        var normalised = username.Trim();
        if (await users.UsernameExistsAsync(normalised, cancellationToken))
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.UsernameAlreadyExists);

        // A role from another tenant must never be assignable — verified against THIS merchant's roles.
        if (roleId is { } id && await roles.FindByIdAsync(merchantId, id, cancellationToken) is null)
            return Result.Failure<MerchantAccountCredential>(MerchantUserErrors.RoleNotInTenant);

        var temporaryPassword = passwordGenerator.Generate();
        var user = MerchantUser.Create(
            merchantId, normalised, displayName, hasher.Hash(temporaryPassword), roleId,
            mustChangePassword: true, timeProvider.GetUtcNow());
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
            .Select(u => new MerchantAccountView(
                u.Id, u.Username, u.DisplayName, u.RoleId,
                u.RoleId is { } rid ? roleNames.GetValueOrDefault(rid) : null,
                u.Status.ToString(), u.MustChangePassword, u.CreatedAt))
            .ToList();

        return Result.Success(views);
    }

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

        user.SetStatus(active ? MerchantUserStatus.Active : MerchantUserStatus.Disabled);
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
