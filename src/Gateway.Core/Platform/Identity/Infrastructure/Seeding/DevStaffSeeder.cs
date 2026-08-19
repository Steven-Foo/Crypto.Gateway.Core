using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Seeding;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Idempotently creates one Admin staff account with the fixed credentials in
/// <see cref="DevStaffSeedOptions"/>. The host registers this only in the Development branch — same
/// convention as <c>DevMerchantSeeder</c>.
/// </summary>
public sealed class DevStaffSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DevStaffSeedOptions> options,
    TimeProvider timeProvider,
    ILogger<DevStaffSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (!seed.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(seed.Username) || string.IsNullOrWhiteSpace(seed.Password))
        {
            logger.LogWarning("Dev staff seed is enabled but Username/Password are missing — skipping.");
            return;
        }

        try
        {
            // IHostedService is always singleton — the repositories/hasher are scoped (EF Core DbContext
            // underneath), so all must come from a scope created here, not the constructor. Same convention
            // as DevMerchantSeeder.
            await using var scope = scopeFactory.CreateAsyncScope();
            var userRepository = scope.ServiceProvider.GetRequiredService<IStaffUserRepository>();
            var roleRepository = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
            var hasher = scope.ServiceProvider.GetRequiredService<IStaffPasswordHasher>();

            if (await userRepository.UsernameExistsAsync(seed.Username, cancellationToken))
            {
                logger.LogInformation("Dev staff user '{Username}' already present.", seed.Username);
                return;
            }

            var adminRoleId = await EnsureAdminRoleAsync(roleRepository, cancellationToken);

            var userResult = StaffUser.Create(seed.Username, hasher.Hash(seed.Password), adminRoleId, timeProvider.GetUtcNow());
            if (userResult.IsFailure)
            {
                logger.LogWarning("Dev staff seed skipped: {Error}.", userResult.Error!.Message);
                return;
            }

            userRepository.Add(userResult.Value);
            await userRepository.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Seeded development staff user '{Username}' (Admin).", seed.Username);
        }
        catch (DbUpdateException)
        {
            logger.LogInformation("Dev staff user '{Username}' already present (concurrent seed).", seed.Username);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Dev staff seeding failed; login will fail until resolved (is the Identity schema migrated?).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Idempotently ensures a wildcard-permission "Admin" role exists, returning its id. The dev
    /// seed's account always gets full access — same guarantee the old hardcoded <c>StaffRole.Admin</c>
    /// enum gave, now expressed as data instead of an enum value.</summary>
    private async Task<Guid> EnsureAdminRoleAsync(IRoleRepository roleRepository, CancellationToken cancellationToken)
    {
        var existing = await roleRepository.FindByNameAsync("Admin", cancellationToken);
        if (existing is not null)
            return existing.Id;

        var role = Role.Create("Admin", "Full access — every permission, present and future.", [Role.WildcardPermission], timeProvider.GetUtcNow());
        roleRepository.Add(role.Value);
        await roleRepository.SaveChangesAsync(cancellationToken);
        return role.Value.Id;
    }
}
