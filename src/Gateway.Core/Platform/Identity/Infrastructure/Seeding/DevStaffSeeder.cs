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
            // IHostedService is always singleton — IStaffUserRepository/IStaffPasswordHasher are scoped
            // (EF Core DbContext underneath), so both must come from a scope created here, not the
            // constructor. Same convention as DevMerchantSeeder.
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IStaffUserRepository>();
            var hasher = scope.ServiceProvider.GetRequiredService<IStaffPasswordHasher>();

            if (await repository.UsernameExistsAsync(seed.Username, cancellationToken))
            {
                logger.LogInformation("Dev staff user '{Username}' already present.", seed.Username);
                return;
            }

            var userResult = StaffUser.Create(seed.Username, hasher.Hash(seed.Password), StaffRole.Admin, timeProvider.GetUtcNow());
            if (userResult.IsFailure)
            {
                logger.LogWarning("Dev staff seed skipped: {Error}.", userResult.Error!.Message);
                return;
            }

            repository.Add(userResult.Value);
            await repository.SaveChangesAsync(cancellationToken);

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
}
