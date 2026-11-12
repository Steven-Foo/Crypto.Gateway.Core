using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

/// <summary>
/// Creates the FIRST staff account on an empty production database — the one account nobody can create from the
/// back office, because creating accounts there needs an account. Run once, by someone with server access, through
/// the Ops host's <c>--bootstrap-admin &lt;username&gt;</c> command.
/// <para>
/// Why a command and not config: a password in a settings file sits on disk for as long as the file does, and a
/// config-driven seeder runs on every boot. Here the password is generated, shown once, and never stored in clear.
/// </para>
/// <para>
/// <b>It refuses once ANY staff account exists</b> (<see cref="StaffUserErrors.BootstrapRefused"/>), so it cannot be
/// used later as a way into a running platform — even by someone who has server access, they would have to empty
/// the staff table first, which leaves its own trail. The account is Admin (wildcard), 2FA required, so the first
/// login is forced through authenticator setup before it can do anything.
/// </para>
/// </summary>
public interface IStaffBootstrapService
{
    Task<Result<StaffAccountCredentialResult>> BootstrapFirstAdminAsync(
        string username, CancellationToken cancellationToken = default);
}

public sealed class StaffBootstrapService(
    IStaffUserRepository userRepository,
    IRoleRepository roleRepository,
    IStaffPasswordHasher passwordHasher,
    IStaffPasswordGenerator passwordGenerator,
    TimeProvider timeProvider) : IStaffBootstrapService
{
    public const string AdminRoleName = "Admin";

    public async Task<Result<StaffAccountCredentialResult>> BootstrapFirstAdminAsync(
        string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Result.Failure<StaffAccountCredentialResult>(StaffUserErrors.UsernameRequired);

        // Any account at all — active or disabled — means the platform already has an owner.
        var (_, existing) = await userRepository.GetPagedAsync(1, 1, cancellationToken);
        if (existing > 0)
            return Result.Failure<StaffAccountCredentialResult>(StaffUserErrors.BootstrapRefused);

        var now = timeProvider.GetUtcNow();
        var role = await roleRepository.FindByNameAsync(AdminRoleName, cancellationToken);
        if (role is null)
        {
            var created = Role.Create(
                AdminRoleName, "Full access — every permission, present and future.", [Role.WildcardPermission], now);
            if (created.IsFailure)
                return Result.Failure<StaffAccountCredentialResult>(created.Error!);

            role = created.Value;
            roleRepository.Add(role);
            await roleRepository.SaveChangesAsync(cancellationToken);
        }

        var password = passwordGenerator.Generate();
        var user = StaffUser.Create(username, passwordHasher.Hash(password), role.Id, requireTwoFactor: true, now);
        if (user.IsFailure)
            return Result.Failure<StaffAccountCredentialResult>(user.Error!);

        userRepository.Add(user.Value);
        await userRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new StaffAccountCredentialResult(user.Value.Id, user.Value.Username, password));
    }
}
