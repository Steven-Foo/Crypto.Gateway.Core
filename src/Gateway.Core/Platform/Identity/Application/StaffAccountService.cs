using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

/// <param name="RequireTwoFactor">The switch: whether this account is ASKED for a second factor.</param>
/// <param name="TwoFactorBound">Whether the account has actually bound an authenticator (an ACTIVE factor —
/// a setup that was started but never confirmed reads false). Independent of the switch: an account can be
/// bound with the switch off, or required but not yet bound. A 2FA reset makes it false again.</param>
public sealed record StaffAccountView(
    Guid StaffUserId, string Username, Guid RoleId, string RoleName, string Status, bool RequireTwoFactor,
    bool TwoFactorBound, DateTimeOffset CreatedAt);

/// <summary>The one-time-visible result of creating an account or resetting its password — mirrors the
/// Merchant module's one-time-secret convention (§10-adjacent).</summary>
public sealed record StaffAccountCredentialResult(Guid StaffUserId, string Username, string Password);

public interface IStaffAccountService
{
    Task<Result<StaffAccountCredentialResult>> CreateAsync(
        string username, Guid roleId, bool requireTwoFactor, CancellationToken cancellationToken = default);

    Task<Result<StaffAccountView>> GetAsync(Guid staffUserId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<StaffAccountView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Refuses to disable the caller's own account (<see cref="StaffUserErrors.CannotDisableSelf"/>)
    /// or the last remaining active account (<see cref="StaffUserErrors.CannotDisableLastActiveAccount"/>).</summary>
    Task<Result<StaffAccountView>> SetStatusAsync(
        Guid staffUserId, bool active, Guid actingStaffUserId, CancellationToken cancellationToken = default);

    Task<Result<StaffAccountView>> ChangeRoleAsync(
        Guid staffUserId, Guid roleId, CancellationToken cancellationToken = default);

    Task<Result<StaffAccountCredentialResult>> ResetPasswordAsync(
        Guid staffUserId, CancellationToken cancellationToken = default);

    /// <summary>Flips the live 2FA-required switch (§ StaffUser.RequireTwoFactor). Refuses to let an admin
    /// change their OWN switch (<see cref="StaffUserErrors.CannotChangeOwnTwoFactorRequirement"/>) — same
    /// self-target guard as <see cref="SetStatusAsync"/>, for the same reason: doing it to yourself would let
    /// you drop out of every guarded action with nobody else's sign-off.</summary>
    Task<Result<StaffAccountView>> SetRequireTwoFactorAsync(
        Guid staffUserId, bool requireTwoFactor, Guid actingStaffUserId, CancellationToken cancellationToken = default);
}

public sealed class StaffAccountService(
    IStaffUserRepository userRepository,
    IRoleRepository roleRepository,
    IStaffTwoFactorRepository twoFactorRepository,
    IStaffPasswordHasher passwordHasher,
    IStaffPasswordGenerator passwordGenerator,
    TimeProvider timeProvider) : IStaffAccountService
{
    public async Task<Result<StaffAccountCredentialResult>> CreateAsync(
        string username, Guid roleId, bool requireTwoFactor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<StaffAccountCredentialResult>(StaffUserErrors.UsernameRequired);

        var trimmed = username.Trim();

        // Pre-check for a friendly error; the unique index remains the real arbiter of a concurrent race.
        if (await userRepository.UsernameExistsAsync(trimmed, cancellationToken))
            return Result.Failure<StaffAccountCredentialResult>(StaffUserErrors.UsernameAlreadyExists);

        if (await roleRepository.GetByIdAsync(roleId, cancellationToken) is null)
            return Result.Failure<StaffAccountCredentialResult>(RoleErrors.NotFound);

        var password = passwordGenerator.Generate();
        var created = StaffUser.Create(
            trimmed, passwordHasher.Hash(password), roleId, requireTwoFactor, timeProvider.GetUtcNow());
        if (created.IsFailure)
            return Result.Failure<StaffAccountCredentialResult>(created.Error!);

        userRepository.Add(created.Value);
        await userRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new StaffAccountCredentialResult(created.Value.Id, created.Value.Username, password));
    }

    public async Task<Result<StaffAccountView>> GetAsync(Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(staffUserId, cancellationToken);
        if (user is null)
            return Result.Failure<StaffAccountView>(StaffUserErrors.NotFound);

        return Result.Success(await ToViewAsync(user, cancellationToken));
    }

    public async Task<(IReadOnlyList<StaffAccountView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (items, total) = await userRepository.GetPagedAsync(page, pageSize, cancellationToken);

        // One read for the whole page rather than one per row. Ids only, no secrets (the staff population is
        // small, so the full enrolled set is cheaper than a paged IN-list).
        var bound = await twoFactorRepository.ListEnrolledStaffUserIdsAsync(cancellationToken);

        var views = new List<StaffAccountView>(items.Count);
        foreach (var user in items)
            views.Add(await ToViewAsync(user, bound.Contains(user.Id), cancellationToken));

        return (views, total);
    }

    public async Task<Result<StaffAccountView>> SetStatusAsync(
        Guid staffUserId, bool active, Guid actingStaffUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(staffUserId, cancellationToken);
        if (user is null)
            return Result.Failure<StaffAccountView>(StaffUserErrors.NotFound);

        if (!active)
        {
            if (user.Id == actingStaffUserId)
                return Result.Failure<StaffAccountView>(StaffUserErrors.CannotDisableSelf);

            // Only the account actually transitioning matters — disabling an already-disabled account is a
            // no-op that shouldn't trip the guard, so only count when this one is currently Active.
            if (user.Status == StaffUserStatus.Active && await userRepository.CountActiveAsync(cancellationToken) <= 1)
                return Result.Failure<StaffAccountView>(StaffUserErrors.CannotDisableLastActiveAccount);
        }

        user.SetStatus(active ? StaffUserStatus.Active : StaffUserStatus.Disabled);
        await userRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(await ToViewAsync(user, cancellationToken));
    }

    public async Task<Result<StaffAccountView>> ChangeRoleAsync(
        Guid staffUserId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(staffUserId, cancellationToken);
        if (user is null)
            return Result.Failure<StaffAccountView>(StaffUserErrors.NotFound);

        if (await roleRepository.GetByIdAsync(roleId, cancellationToken) is null)
            return Result.Failure<StaffAccountView>(RoleErrors.NotFound);

        var result = user.ChangeRole(roleId);
        if (result.IsFailure)
            return Result.Failure<StaffAccountView>(result.Error!);

        await userRepository.SaveChangesAsync(cancellationToken);
        return Result.Success(await ToViewAsync(user, cancellationToken));
    }

    public async Task<Result<StaffAccountCredentialResult>> ResetPasswordAsync(
        Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(staffUserId, cancellationToken);
        if (user is null)
            return Result.Failure<StaffAccountCredentialResult>(StaffUserErrors.NotFound);

        var password = passwordGenerator.Generate();
        var result = user.ResetPassword(passwordHasher.Hash(password));
        if (result.IsFailure)
            return Result.Failure<StaffAccountCredentialResult>(result.Error!);

        await userRepository.SaveChangesAsync(cancellationToken);
        return Result.Success(new StaffAccountCredentialResult(user.Id, user.Username, password));
    }

    public async Task<Result<StaffAccountView>> SetRequireTwoFactorAsync(
        Guid staffUserId, bool requireTwoFactor, Guid actingStaffUserId, CancellationToken cancellationToken = default)
    {
        if (staffUserId == actingStaffUserId)
            return Result.Failure<StaffAccountView>(StaffUserErrors.CannotChangeOwnTwoFactorRequirement);

        var user = await userRepository.GetByIdAsync(staffUserId, cancellationToken);
        if (user is null)
            return Result.Failure<StaffAccountView>(StaffUserErrors.NotFound);

        user.SetRequireTwoFactor(requireTwoFactor);
        await userRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(await ToViewAsync(user, cancellationToken));
    }

    private async Task<StaffAccountView> ToViewAsync(StaffUser user, CancellationToken cancellationToken)
    {
        var factor = await twoFactorRepository.FindByStaffUserIdAsync(user.Id, cancellationToken);
        return await ToViewAsync(user, factor?.IsEnrolled == true, cancellationToken);
    }

    private async Task<StaffAccountView> ToViewAsync(StaffUser user, bool twoFactorBound, CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetByIdAsync(user.RoleId, cancellationToken);
        return new StaffAccountView(
            user.Id, user.Username, user.RoleId, role?.Name ?? "(unknown role)", user.Status.ToString(),
            user.RequireTwoFactor, twoFactorBound, user.CreatedAt);
    }
}
