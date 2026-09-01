using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Seeding;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Idempotently creates one merchant-portal login bound to the dev merchant named by
/// <see cref="DevMerchantPortalSeedOptions.MerchantCode"/>, plus that tenant's built-in Administrator role, so a
/// fresh clone can sign in and manage exactly that merchant. Resolves the merchant by code through
/// <c>IMerchantDirectory</c> (§4.5) — it must already be seeded; if it isn't yet, this warns and skips rather
/// than bricking boot. Never a real credential (§10). Same warn-and-continue convention as <c>DevStaffSeeder</c>.
/// </summary>
public sealed class DevMerchantPortalSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DevMerchantPortalSeedOptions> options,
    TimeProvider timeProvider,
    ILogger<DevMerchantPortalSeeder> logger) : IHostedService
{
    private const string AdministratorRoleName = "Administrator";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (!seed.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(seed.Username) || string.IsNullOrWhiteSpace(seed.Password) ||
            string.IsNullOrWhiteSpace(seed.MerchantCode))
        {
            logger.LogWarning("Dev merchant-portal seed is enabled but Username/Password/MerchantCode are missing — skipping.");
            return;
        }

        // The primary login, then any additional demo tenants. Each is seeded independently: one that cannot be
        // bound (its merchant is not seeded yet) must not stop the others.
        var logins = new List<DevMerchantPortalLogin>
        {
            new() { MerchantCode = seed.MerchantCode, Username = seed.Username, Password = seed.Password, DisplayName = seed.DisplayName },
        };
        logins.AddRange(seed.AdditionalLogins.Where(l =>
            !string.IsNullOrWhiteSpace(l.MerchantCode) &&
            !string.IsNullOrWhiteSpace(l.Username) &&
            !string.IsNullOrWhiteSpace(l.Password)));

        foreach (var login in logins)
            await SeedLoginAsync(login, cancellationToken);
    }

    private async Task SeedLoginAsync(DevMerchantPortalLogin seed, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var merchants = scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
            var userRepository = scope.ServiceProvider.GetRequiredService<IMerchantUserRepository>();
            var roleRepository = scope.ServiceProvider.GetRequiredService<IMerchantRoleRepository>();
            var hasher = scope.ServiceProvider.GetRequiredService<IMerchantPasswordHasher>();

            var merchant = await merchants.FindByCodeAsync(seed.MerchantCode, cancellationToken);
            if (merchant is null)
            {
                logger.LogWarning(
                    "Dev merchant-portal seed skipped: merchant '{MerchantCode}' not found — run the merchant dev seed first.",
                    seed.MerchantCode);
                return;
            }

            var adminRoleId = await EnsureAdministratorRoleAsync(roleRepository, merchant.MerchantId, cancellationToken);

            var existing = await userRepository.FindByUsernameAsync(seed.Username, cancellationToken);
            if (existing is not null)
            {
                // Idempotent UPGRADE path: a login seeded before roles existed has no RoleId, which is
                // fail-closed (no permissions). Bind it to the Administrator role so an existing dev DB keeps
                // working after this migration instead of silently losing access.
                if (existing.RoleId is null)
                {
                    existing.AssignRole(adminRoleId);
                    await userRepository.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Dev merchant-portal user '{Username}' adopted the {Role} role.", seed.Username, AdministratorRoleName);
                }
                else
                {
                    logger.LogInformation("Dev merchant-portal user '{Username}' already present.", seed.Username);
                }

                return;
            }

            var userResult = MerchantUser.Create(
                merchant.MerchantId, seed.Username, seed.DisplayName, hasher.Hash(seed.Password), adminRoleId,
                mustChangePassword: false, timeProvider.GetUtcNow());
            if (userResult.IsFailure)
            {
                logger.LogWarning("Dev merchant-portal seed skipped: {Error}.", userResult.Error!.Message);
                return;
            }

            userRepository.Add(userResult.Value);
            await userRepository.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Seeded development merchant-portal user '{Username}' ({Role}) for merchant '{MerchantCode}'.",
                seed.Username, AdministratorRoleName, seed.MerchantCode);
        }
        catch (DbUpdateException)
        {
            logger.LogInformation("Dev merchant-portal user '{Username}' already present (concurrent seed).", seed.Username);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Dev merchant-portal seeding failed; portal login will fail until resolved (is the MerchantIdentity schema migrated?).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Idempotently ensures this tenant has a wildcard Administrator role, returning its id — the
    /// per-merchant equivalent of the staff Admin role, so a merchant always has one account that cannot be
    /// locked out of its own portal.</summary>
    private async Task<Guid> EnsureAdministratorRoleAsync(
        IMerchantRoleRepository roles, Guid merchantId, CancellationToken cancellationToken)
    {
        var existing = await roles.FindByNameAsync(merchantId, AdministratorRoleName, cancellationToken);
        if (existing is not null)
            return existing.Id;

        var role = MerchantRole.Create(
            merchantId, AdministratorRoleName, "Full portal access — every permission, present and future.",
            [MerchantRole.WildcardPermission], timeProvider.GetUtcNow());

        roles.Add(role.Value);
        await roles.SaveChangesAsync(cancellationToken);
        return role.Value.Id;
    }
}
