using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Workers;

/// <summary>Poll interval for the callback delivery worker. Supplied by the host.</summary>
public sealed class CallbackDeliveryWorkerOptions
{
    /// <summary>Deliberately shorter than the schedule's shortest gap (30s, § CallbackDeliveryProcessingBackoff)
    /// so a due retry fires close to on-time, not just eventually.</summary>
    public TimeSpan ProcessInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Drives due callback deliveries through the bounded backoff schedule (§9, § CallbackDeliveryProcessingBackoff).
/// Registered by <c>AddNotificationWorkers</c>, alongside the <see cref="CallbackDeliveryProcessingService"/>
/// it resolves — kept out of <c>AddNotificationModule</c> so a read-only composer (Ops) never runs it.
/// </summary>
public sealed class CallbackDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    CallbackDeliveryWorkerOptions options,
    ILogger<CallbackDeliveryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(options.ProcessInterval, stoppingToken, logger, "callback delivery", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processed = await scope.ServiceProvider.GetRequiredService<CallbackDeliveryProcessingService>().ProcessOnceAsync(ct);
            if (processed > 0)
                logger.LogInformation("Processed {Count} due callback(s).", processed);
        });
}

internal static class WorkerLoop
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
