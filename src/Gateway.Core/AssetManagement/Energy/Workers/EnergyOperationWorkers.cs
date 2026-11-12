using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

/// <summary>5b: keeps the staking wallet topped up — each pass asks <see cref="StakingService"/> to queue an
/// auto-stake if the wallet's energy is at/below its policy threshold. Read-and-maybe-create; no money moved.</summary>
public sealed class StakeReplenishWorker(
    IServiceScopeFactory scopeFactory,
    EnergyWorkerOptions options,
    ILogger<StakeReplenishWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        EnergyWorkerLoop.RunAsync(options.StakeReplenishInterval, stoppingToken, logger, "stake replenish", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<StakingService>();
            foreach (var chain in options.Chains)
                await service.ReplenishAsync(chain, ct);
        });
}

/// <summary>5b: drives Pending stake/delegate operations through build → sign → broadcast (§9).</summary>
public sealed class EnergyOperationProcessingWorker(
    IServiceScopeFactory scopeFactory,
    EnergyWorkerOptions options,
    ILogger<EnergyOperationProcessingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        EnergyWorkerLoop.RunAsync(options.OperationProcessInterval, stoppingToken, logger, "energy operation processing", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processed = await scope.ServiceProvider.GetRequiredService<EnergyOperationProcessingService>().ProcessOnceAsync(ct);
            if (processed > 0)
                logger.LogInformation("Processed {Count} energy operation(s).", processed);
        });
}

/// <summary>5b: confirms broadcast stake/delegate operations once buried under the policy depth (§9). No ledger settle.</summary>
public sealed class EnergyOperationConfirmationWorker(
    IServiceScopeFactory scopeFactory,
    EnergyWorkerOptions options,
    ILogger<EnergyOperationConfirmationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        EnergyWorkerLoop.RunAsync(options.OperationConfirmationInterval, stoppingToken, logger, "energy operation confirmation", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<EnergyOperationConfirmationService>().TrackOnceAsync(ct);
            if (changed > 0)
                logger.LogInformation("{Count} energy operation(s) confirmed.", changed);
        });
}

/// <summary>
/// Takes back energy lent to deposit addresses that no longer need it (<see cref="EnergyReclaimService"/>).
/// Creates Undelegate operations only; the processing worker signs and sends them. Runs in the money host only.
/// Two instances running at once can at worst queue the same take-back twice; the second is refused by the
/// chain (nothing left to take back) and recorded as failed, so no TRX is affected.
/// </summary>
public sealed class EnergyReclaimWorker(
    IServiceScopeFactory scopeFactory,
    EnergyWorkerOptions options,
    ILogger<EnergyReclaimWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        EnergyWorkerLoop.RunAsync(options.ReclaimInterval, stoppingToken, logger, "energy take-back", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<EnergyReclaimService>();
            foreach (var chain in options.Chains)
            {
                var queued = await service.ReclaimOnceAsync(chain, ct);
                if (queued > 0)
                    logger.LogInformation("Queued {Count} energy take-back(s) on {Chain}.", queued, chain);
            }
        });
}

internal static class EnergyWorkerLoop
{
    public static async Task RunAsync(
        TimeSpan interval, CancellationToken stoppingToken, ILogger logger, string name, Func<CancellationToken, Task> pass)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await pass(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Worker} pass failed; will retry next tick.", name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
