using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Workers;

/// <summary>Poll intervals for the withdrawal workers. Supplied by the host.</summary>
public sealed class WithdrawalWorkerOptions
{
    public TimeSpan ProcessInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConfirmationInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Drives approved withdrawals through build → sign → broadcast (§9). Idempotent per pass; a failure is logged and retried.</summary>
public sealed class WithdrawalProcessingWorker(
    IServiceScopeFactory scopeFactory,
    WithdrawalWorkerOptions options,
    ILogger<WithdrawalProcessingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(options.ProcessInterval, stoppingToken, logger, "withdrawal processing", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processed = await scope.ServiceProvider.GetRequiredService<WithdrawalProcessingService>().ProcessOnceAsync(ct);
            if (processed > 0)
                logger.LogInformation("Processed {Count} withdrawal(s).", processed);
        });
}

/// <summary>Confirms broadcast withdrawals and triggers ledger settlement (§9).</summary>
public sealed class WithdrawalConfirmationWorker(
    IServiceScopeFactory scopeFactory,
    WithdrawalWorkerOptions options,
    ILogger<WithdrawalConfirmationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(options.ConfirmationInterval, stoppingToken, logger, "withdrawal confirmation", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<WithdrawalConfirmationService>().TrackOnceAsync(ct);
            if (changed > 0)
                logger.LogInformation("{Count} withdrawal(s) confirmed.", changed);
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
