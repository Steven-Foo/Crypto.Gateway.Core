using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier ONLY. On host boot, idempotently ensures the platform hot withdrawal pool described by
/// <see cref="TreasuryDevHotWalletOptions.HotWalletPool"/> exists — deriving and registering the pool's child
/// wallets from the one platform withdrawal HD wallet — so a signed <c>/withdraw</c> can source a hot wallet
/// from the database. Registered only alongside the in-memory secret provider (testnet tier); in production
/// the pool is provisioned through an ops action backed by a KMS, not seeded here (§10).
/// </summary>
public sealed class TreasuryHotWalletSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<TreasuryDevHotWalletOptions> options,
    ILogger<TreasuryHotWalletSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var seed in options.Value.HotWalletPool)
        {
            if (!Enum.TryParse<Chain>(seed.Chain, ignoreCase: true, out var chain))
            {
                logger.LogWarning("Treasury hot-wallet pool seed skipped: unknown chain '{Chain}'.", seed.Chain);
                continue;
            }

            try
            {
                // A scope per chain keeps each provisioning pass on its own DbContext (the cross-module
                // registrars are scoped), so one failure never strands a tracked entity on the next.
                await using var scope = scopeFactory.CreateAsyncScope();
                var provisioning = scope.ServiceProvider.GetRequiredService<TreasuryHotWalletProvisioningService>();

                var result = await provisioning.EnsurePoolAsync(chain, seed.Size, cancellationToken);
                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Treasury hot-wallet pool seed for {Chain} (size {Size}) skipped: {Error}.",
                        chain, seed.Size, result.Error!.Message);
                }
            }
            catch (Exception ex)
            {
                // DEV convenience must never brick host startup. The usual cause is an un-migrated schema:
                // log an actionable warning and carry on — withdrawal stays inert for this chain until fixed.
                logger.LogWarning(ex,
                    "Treasury hot-wallet pool seeding for {Chain} failed; withdrawal will be inert for this chain "
                    + "until resolved (are the Wallet + KeyManagement schemas migrated on this database?).",
                    chain);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
