using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

public sealed record RoleView(Guid RoleId, string Name, string? Description, IReadOnlyList<string> PermissionCodes, DateTimeOffset CreatedAt);

public interface IRoleService
{
    Task<Result<RoleView>> CreateAsync(
        string name, string? description, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default);

    Task<Result<RoleView>> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<RoleView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    Task<Result<RoleView>> UpdateDetailsAsync(
        Guid roleId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Replaces the role's permission set outright — the caller sends the full desired set, not a diff.</summary>
    Task<Result<RoleView>> SetPermissionsAsync(
        Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default);

    /// <summary>Refused with <see cref="RoleErrors.InUse"/> while any staff account still references this role.</summary>
    Task<Result> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);
}

public sealed class RoleService(IRoleRepository repository, TimeProvider timeProvider) : IRoleService
{
    public async Task<Result<RoleView>> CreateAsync(
        string name, string? description, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();

        // Pre-check for a friendly error; a unique index remains the real arbiter of a concurrent create race.
        if (await repository.NameExistsAsync(trimmed, cancellationToken))
            return Result.Failure<RoleView>(RoleErrors.NameAlreadyExists);

        var created = Role.Create(trimmed, description, permissionCodes, timeProvider.GetUtcNow());
        if (created.IsFailure)
            return Result.Failure<RoleView>(created.Error!);

        repository.Add(created.Value);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(ToView(created.Value));
    }

    public async Task<Result<RoleView>> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetByIdAsync(roleId, cancellationToken);
        return role is null
            ? Result.Failure<RoleView>(RoleErrors.NotFound)
            : Result.Success(ToView(role));
    }

    public async Task<(IReadOnlyList<RoleView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (items, total) = await repository.GetPagedAsync(page, pageSize, cancellationToken);
        return (items.Select(ToView).ToList(), total);
    }

    public async Task<Result<RoleView>> UpdateDetailsAsync(
        Guid roleId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetByIdAsync(roleId, cancellationToken);
        if (role is null)
            return Result.Failure<RoleView>(RoleErrors.NotFound);

        var trimmed = name.Trim();
        if (!string.Equals(trimmed, role.Name, StringComparison.Ordinal) && await repository.NameExistsAsync(trimmed, cancellationToken))
            return Result.Failure<RoleView>(RoleErrors.NameAlreadyExists);

        var result = role.UpdateDetails(trimmed, description, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return Result.Failure<RoleView>(result.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(role));
    }

    public async Task<Result<RoleView>> SetPermissionsAsync(
        Guid roleId, IReadOnlyCollection<string> permissionCodes, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetByIdAsync(roleId, cancellationToken);
        if (role is null)
            return Result.Failure<RoleView>(RoleErrors.NotFound);

        role.SetPermissions(permissionCodes, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(ToView(role));
    }

    public async Task<Result> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetByIdAsync(roleId, cancellationToken);
        if (role is null)
            return Result.Failure(RoleErrors.NotFound);

        if (await repository.IsAssignedToAnyStaffUserAsync(roleId, cancellationToken))
            return Result.Failure(RoleErrors.InUse);

        repository.Remove(role);
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static RoleView ToView(Role role) => new(role.Id, role.Name, role.Description, role.PermissionCodes, role.CreatedAt);
}
