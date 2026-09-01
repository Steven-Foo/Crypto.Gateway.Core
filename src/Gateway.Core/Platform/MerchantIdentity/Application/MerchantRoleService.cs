using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed record MerchantRoleView(
    Guid RoleId, string Name, string? Description, IReadOnlyList<string> PermissionCodes, DateTimeOffset CreatedAt);

/// <summary>
/// Per-tenant role management for the merchant portal. <b>Every method takes the caller's <c>merchantId</c> as
/// its first argument and scopes every lookup by it</b> — a merchant admin can only see, edit, or delete roles
/// its own merchant owns, and a role id from another tenant simply reads as "not found".
///
/// <para>Permission codes arrive as opaque strings: the host validates them against its portal catalog before
/// calling in, which is what stops a merchant granting itself a code the portal doesn't define (or a
/// platform/ops code, which lives in a different catalog on a different host entirely).</para>
/// </summary>
public interface IMerchantRoleService
{
    Task<Result<MerchantRoleView>> CreateAsync(
        Guid merchantId, string name, string? description, IReadOnlyCollection<string> permissionCodes,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MerchantRoleView>>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<Result<MerchantRoleView>> UpdateAsync(
        Guid merchantId, Guid roleId, string name, string? description, CancellationToken cancellationToken = default);

    Task<Result<MerchantRoleView>> SetPermissionsAsync(
        Guid merchantId, Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default);
}

public sealed class MerchantRoleService(
    IMerchantRoleRepository roles, IMerchantUserRepository users, TimeProvider timeProvider) : IMerchantRoleService
{
    public async Task<Result<MerchantRoleView>> CreateAsync(
        Guid merchantId, string name, string? description, IReadOnlyCollection<string> permissionCodes,
        CancellationToken cancellationToken = default)
    {
        if (await roles.FindByNameAsync(merchantId, name.Trim(), cancellationToken) is not null)
            return Result.Failure<MerchantRoleView>(MerchantRoleErrors.NameAlreadyExists);

        var role = MerchantRole.Create(merchantId, name, description, permissionCodes, timeProvider.GetUtcNow());
        if (role.IsFailure)
            return Result.Failure<MerchantRoleView>(role.Error!);

        roles.Add(role.Value);
        await roles.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(role.Value));
    }

    public async Task<Result<IReadOnlyList<MerchantRoleView>>> ListAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var items = await roles.ListAsync(merchantId, cancellationToken);
        return Result.Success<IReadOnlyList<MerchantRoleView>>(items.Select(ToView).ToList());
    }

    public async Task<Result<MerchantRoleView>> UpdateAsync(
        Guid merchantId, Guid roleId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var role = await roles.FindByIdAsync(merchantId, roleId, cancellationToken);
        if (role is null)
            return Result.Failure<MerchantRoleView>(MerchantRoleErrors.NotFound);

        var clash = await roles.FindByNameAsync(merchantId, name.Trim(), cancellationToken);
        if (clash is not null && clash.Id != roleId)
            return Result.Failure<MerchantRoleView>(MerchantRoleErrors.NameAlreadyExists);

        var result = role.UpdateDetails(name, description, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return Result.Failure<MerchantRoleView>(result.Error!);

        await roles.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(role));
    }

    public async Task<Result<MerchantRoleView>> SetPermissionsAsync(
        Guid merchantId, Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default)
    {
        var role = await roles.FindByIdAsync(merchantId, roleId, cancellationToken);
        if (role is null)
            return Result.Failure<MerchantRoleView>(MerchantRoleErrors.NotFound);

        role.SetPermissions(permissionCodes, timeProvider.GetUtcNow());
        await roles.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(role));
    }

    public async Task<Result> DeleteAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roles.FindByIdAsync(merchantId, roleId, cancellationToken);
        if (role is null)
            return Result.Failure(MerchantRoleErrors.NotFound);

        // Refuse while accounts still reference it — deleting would silently strip their access (or orphan the
        // reference). The caller reassigns first. Same precheck the staff RoleService does.
        if (await users.CountByRoleAsync(merchantId, roleId, cancellationToken) > 0)
            return Result.Failure(MerchantRoleErrors.InUse);

        roles.Remove(role);
        await roles.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static MerchantRoleView ToView(MerchantRole role) =>
        new(role.Id, role.Name, role.Description, role.PermissionCodes, role.CreatedAt);
}
