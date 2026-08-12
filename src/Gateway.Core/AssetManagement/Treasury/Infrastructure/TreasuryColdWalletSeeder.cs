using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier ONLY. On host boot, idempotently registers the cold treasury address(es) from
/// <see cref="TreasuryDevColdWalletOptions.ColdWallets"/>, so Sweep has a destination and Reconciliation a
/// controlled address to sum. In production the cold address is registered via the staff ops action (§10).
/// </summary>
public sealed class TreasuryColdWalletSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<TreasuryDevColdWalletOptions> options,
    ILogger<TreasuryColdWalletSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var seed in options.Value.ColdWallets)
        {
            if (!Enum.TryParse<Chain>(seed.Chain, ignoreCase: true, out var chain))
            {
                logger.LogWarning("Treasury cold-wallet seed skipped: unknown chain '{Chain}'.", seed.Chain);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var registrar = scope.ServiceProvider.GetRequiredService<ITreasuryColdWalletRegistrar>();
                var result = await registrar.RegisterAsync(chain, seed.Address, cancellationToken);
                if (result.IsFailure)
                    logger.LogWarning("Treasury cold-wallet seed for {Chain} skipped: {Error}.", chain, result.Error!.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Treasury cold-wallet seeding for {Chain} failed (is the treasury schema migrated on this database?).", chain);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
