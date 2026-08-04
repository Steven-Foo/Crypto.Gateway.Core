using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier ONLY. Idempotently registers the platform hot withdrawal wallet(s) described by
/// <see cref="TreasuryDevHotWalletOptions.DevHotWallets"/> on host boot, so a signed <c>/withdraw</c> can
/// source its hot wallet from the database (not raw config). Registered only alongside the in-memory secret
/// provider (testnet tier); in production a hot wallet would be registered through an ops/admin action
/// backed by a KMS, not seeded from config (§10).
/// </summary>
public sealed class TreasuryHotWalletSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<TreasuryDevHotWalletOptions> options,
    ILogger<TreasuryHotWalletSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var seed in options.Value.DevHotWallets)
        {
            if (!Enum.TryParse<Chain>(seed.Chain, ignoreCase: true, out var chain))
            {
                logger.LogWarning("Treasury hot-wallet seed skipped: unknown chain '{Chain}'.", seed.Chain);
                continue;
            }

            try
            {
                // A scope per seed keeps each registration on its own DbContext (the cross-module registrars
                // are scoped), so one failure never strands a tracked entity on the next seed.
                await using var scope = scopeFactory.CreateAsyncScope();
                var provisioning = scope.ServiceProvider.GetRequiredService<TreasuryHotWalletProvisioningService>();

                var result = await provisioning.ProvisionHotWalletAsync(
                    chain, seed.Address, seed.SecretReference, seed.Description, cancellationToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Treasury hot-wallet seed for {Chain} ({Address}) skipped: {Error}.",
                        chain, seed.Address, result.Error!.Message);
                }
            }
            catch (Exception ex)
            {
                // DEV convenience must never brick host startup. The usual cause is an un-migrated schema:
                // log an actionable warning and carry on — withdrawal stays inert for this chain until fixed.
                logger.LogWarning(ex,
                    "Treasury hot-wallet seeding for {Chain} ({Address}) failed; withdrawal will be inert for this "
                    + "chain until resolved (are the Wallet + KeyManagement schemas migrated on this database?).",
                    chain, seed.Address);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
