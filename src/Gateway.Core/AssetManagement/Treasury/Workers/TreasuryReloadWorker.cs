using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Workers;

public sealed class TreasuryReloadWorkerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Broadcasts submitted treasury reloads and confirms them (§9). Single-flighted across host instances so two
/// workers don't both re-broadcast the same reload — the DB (rowversion) remains the correctness guard, the
/// lock is just performance (§7.4). Idempotent per pass; a failure is logged and retried.
/// </summary>
public sealed class TreasuryReloadWorker(
    IServiceScopeFactory scopeFactory,
    TreasuryReloadWorkerOptions options,
    ILogger<TreasuryReloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Treasury reload pass failed; will retry next tick.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var lockFactory = scope.ServiceProvider.GetRequiredService<IDistributedLockFactory>();

        IAsyncDisposable handle;
        try
        {
            handle = await lockFactory.AcquireAsync("treasury:reload", TimeSpan.Zero, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // another instance holds the lock (or the backend is down) — skip; retried next tick
        }

        await using (handle)
        {
            var changed = await scope.ServiceProvider.GetRequiredService<TreasuryReloadProcessingService>().ProcessOnceAsync(cancellationToken);
            if (changed > 0)
                logger.LogInformation("{Count} treasury reload(s) advanced.", changed);
        }
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

public static class TreasuryWorkersExtensions
{
    /// <summary>Registers the treasury reload processing service + its worker. The host calls this after
    /// registering the Blockchain broadcaster/chain-status ports the processing service needs.</summary>
    public static IServiceCollection AddTreasuryReloadWorker(this IServiceCollection services, TreasuryReloadWorkerOptions options)
    {
        services.AddScoped<TreasuryReloadProcessingService>();
        services.TryAddSingleton(new TreasuryReloadOptions()); // confirmation depth (dev default 1); host may override first
        services.AddSingleton(options);
        services.AddHostedService<TreasuryReloadWorker>();
        return services;
    }
}
