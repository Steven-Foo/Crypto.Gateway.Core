using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

/// <summary>
/// Phase 5a resource monitor (§9): every <see cref="EnergyWorkerOptions.MonitorInterval"/>, samples each
/// platform wallet's on-chain resources, records an observability snapshot + history, and alerts on
/// Low/Critical energy. A fresh DI scope per pass gives it a clean scoped DbContext; each pass is a pure
/// read-and-record and so is inherently idempotent and resumable. One chain's failure is logged and never
/// stops the others or the loop. It moves no money and holds no keys.
/// </summary>
public sealed class ResourceMonitorWorker(
    IServiceScopeFactory scopeFactory,
    EnergyWorkerOptions options,
    ILogger<ResourceMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.MonitorInterval);

        do
        {
            foreach (var chain in options.Chains)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var monitor = scope.ServiceProvider.GetRequiredService<ResourceMonitorService>();
                    await monitor.MonitorAsync(chain, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Resource monitor pass failed for {Chain}; will retry next tick.", chain);
                }
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
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
