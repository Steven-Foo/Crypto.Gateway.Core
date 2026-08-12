using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Workers;

/// <summary>Chains + poll intervals for the sweep workers. Supplied by the host.</summary>
public sealed class SweepWorkerOptions
{
    /// <summary>Chains to sweep. TRON-USDT at launch, so this is <c>[Chain.Tron]</c> today.</summary>
    public IReadOnlyList<Chain> Chains { get; init; } = [];

    /// <summary>How often to scan deposit addresses for balances worth sweeping. Slower than processing —
    /// scanning reads on-chain balances (RPC per funded address), and sweeping is not latency-sensitive.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan ProcessInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ConfirmationInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Scans funded deposit addresses and creates sweeps for those over the threshold (§9). Read-only
/// per pass except for the sweep rows it inserts; idempotent (the one-in-flight unique index arbitrates).</summary>
public sealed class SweepScanWorker(
    IServiceScopeFactory scopeFactory,
    SweepWorkerOptions options,
    ILogger<SweepScanWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        SweepWorkerLoop.RunAsync(options.ScanInterval, stoppingToken, logger, "sweep scan", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<SweepScanService>();
            foreach (var chain in options.Chains)
            {
                var created = await service.ScanAsync(chain, ct);
                if (created > 0)
                    logger.LogInformation("Created {Count} sweep(s) on {Chain}.", created, chain);
            }
        });
}

/// <summary>Drives Pending sweeps through build → sign → broadcast (§9). Idempotent per pass; a failure is logged and retried.</summary>
public sealed class SweepProcessingWorker(
    IServiceScopeFactory scopeFactory,
    SweepWorkerOptions options,
    ILogger<SweepProcessingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        SweepWorkerLoop.RunAsync(options.ProcessInterval, stoppingToken, logger, "sweep processing", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processed = await scope.ServiceProvider.GetRequiredService<SweepProcessingService>().ProcessOnceAsync(ct);
            if (processed > 0)
                logger.LogInformation("Processed {Count} sweep(s).", processed);
        });
}

/// <summary>Confirms broadcast sweeps once buried under the policy depth (§9). No ledger settle — custody was unchanged.</summary>
public sealed class SweepConfirmationWorker(
    IServiceScopeFactory scopeFactory,
    SweepWorkerOptions options,
    ILogger<SweepConfirmationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        SweepWorkerLoop.RunAsync(options.ConfirmationInterval, stoppingToken, logger, "sweep confirmation", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<SweepConfirmationService>().TrackOnceAsync(ct);
            if (changed > 0)
                logger.LogInformation("{Count} sweep(s) confirmed.", changed);
        });
}

internal static class SweepWorkerLoop
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
