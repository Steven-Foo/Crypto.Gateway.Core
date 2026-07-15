using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Idempotently creates the HD-wallet rows described by
/// <see cref="DevelopmentKeyCustodyOptions.DevWallets"/> so that a signed <c>/deposit</c> can provision an
/// address on a fresh clone, with no manual database seeding.
///
/// Every wallet it creates is <see cref="SecretProviderKind.InMemoryDevelopment"/>, so it can never
/// resolve for a production wallet row (§10). The host registers this only in the Development branch; it
/// is absent in production, where real HD-wallet rows are provisioned against a KMS instead.
/// </summary>
public sealed class DevHdWalletSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<DevelopmentKeyCustodyOptions> options,
    ILogger<DevHdWalletSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeds = options.Value.DevWallets;

        foreach (var seed in seeds)
        {
            if (!Enum.TryParse<Chain>(seed.Chain, ignoreCase: true, out var chain))
            {
                logger.LogWarning("Dev HD-wallet seed '{Name}' skipped: unknown chain '{Chain}'.", seed.Name, seed.Chain);
                continue;
            }

            if (!Enum.TryParse<HdWalletPurpose>(seed.Purpose, ignoreCase: true, out var purpose))
            {
                logger.LogWarning("Dev HD-wallet seed '{Name}' skipped: unknown purpose '{Purpose}'.", seed.Name, seed.Purpose);
                continue;
            }

            try
            {
                // A scope per seed keeps each insert on its own DbContext, so one failure never strands a
                // tracked entity on the next seed's SaveChanges.
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();

                if (await repository.FindActiveAsync(chain, purpose, cancellationToken) is not null)
                    continue; // Already present — idempotent across restarts.

                var created = HdWallet.Create(
                    seed.Name, chain, purpose,
                    SecretProviderKind.InMemoryDevelopment, seed.SecretReference, seed.PublicKeyReference,
                    seed.DerivationPath);

                if (created.IsFailure)
                {
                    logger.LogWarning("Dev HD-wallet seed '{Name}' skipped: {Error}.", seed.Name, created.Error!.Message);
                    continue;
                }

                repository.Add(created.Value);
                await repository.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Seeded development HD wallet '{Name}' ({Chain}/{Purpose}).", seed.Name, chain, purpose);
            }
            catch (DbUpdateException)
            {
                // A concurrent host instance won the unique (Chain, Purpose) slot first. Harmless.
                logger.LogInformation("Development HD wallet '{Name}' ({Chain}/{Purpose}) already present.", seed.Name, chain, purpose);
            }
            catch (Exception ex)
            {
                // DEV convenience must never brick host startup. The usual cause is an un-migrated schema:
                // log an actionable warning and carry on — /deposit provisioning stays unavailable until fixed.
                logger.LogWarning(ex,
                    "Dev HD-wallet seeding for '{Name}' failed; /deposit provisioning will be unavailable until resolved "
                    + "(is the KeyManagement schema migrated on this database?).", seed.Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
