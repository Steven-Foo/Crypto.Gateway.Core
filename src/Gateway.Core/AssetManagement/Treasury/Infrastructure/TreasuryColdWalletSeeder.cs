using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
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

            if (!Enum.TryParse<ColdWalletKind>(seed.Kind ?? nameof(ColdWalletKind.Safe), ignoreCase: true, out var kind))
            {
                logger.LogWarning(
                    "Treasury cold-wallet seed for {Chain} skipped: unknown kind '{Kind}'.", chain, seed.Kind);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var registrar = scope.ServiceProvider.GetRequiredService<ITreasuryColdWalletRegistrar>();

                // Activate: the seed IS the dev destination, and registering without designating would leave
                // sweeps inert on a fresh environment. Idempotent — re-registering the same address adopts it.
                var result = await registrar.RegisterAsync(
                    new RegisterColdWalletCommand(chain, kind, seed.Address, seed.Label, Activate: true),
                    cancellationToken);
                if (result.IsFailure)
                {
                    // The shipped placeholder is deliberately NOT a valid address, so a fresh clone cannot
                    // sweep real funds to a made-up destination. Sweeping stays inert until a developer puts
                    // a real watch-only address in appsettings.Local.json — the same posture as an absent
                    // signer (§10). Say so, rather than leaving someone to wonder why nothing sweeps.
                    logger.LogWarning(
                        "Treasury cold-wallet seed for {Chain}/{Kind} skipped: {Error} Sweeps stay inert for "
                        + "this chain until Treasury:ColdWallets holds a real watch-only address "
                        + "(set it in the git-ignored appsettings.Local.json).",
                        chain, kind, result.Error!.Message);
                }
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
