using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Infrastructure.Locking;
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

/// <summary>Drives approved withdrawals through build → sign → broadcast (§9). Idempotent per pass; a failure is logged and retried.
/// Registered by <c>AddWithdrawalWorkers</c>, alongside the <see cref="WithdrawalProcessingService"/> it resolves.</summary>
public sealed class WithdrawalProcessingWorker(
    IServiceScopeFactory scopeFactory,
    WithdrawalWorkerOptions options,
    ILogger<WithdrawalProcessingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(options.ProcessInterval, stoppingToken, logger, "withdrawal processing", async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            // Single-flight across host instances so two workers don't redundantly build/sign the same
            // withdrawals or contend on the pool (the DB unique index + rowversion stay the correctness guard
            // regardless, §7.4). Skipped this tick if another instance holds the lock.
            await WorkerLoop.SingleFlightAsync(
                scope.ServiceProvider.GetRequiredService<IDistributedLockFactory>(), "withdrawal:processing", ct, async () =>
                {
                    var processed = await scope.ServiceProvider.GetRequiredService<WithdrawalProcessingService>().ProcessOnceAsync(ct);
                    if (processed > 0)
                        logger.LogInformation("Processed {Count} withdrawal(s).", processed);
                });
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
            // Single-flight confirmation too, so two instances don't both re-read the same broadcast txs
            // (rowversion already prevents a double-settle; this just avoids the wasted work).
            await WorkerLoop.SingleFlightAsync(
                scope.ServiceProvider.GetRequiredService<IDistributedLockFactory>(), "withdrawal:confirmation", ct, async () =>
                {
                    var changed = await scope.ServiceProvider.GetRequiredService<WithdrawalConfirmationService>().TrackOnceAsync(ct);
                    if (changed > 0)
                        logger.LogInformation("{Count} withdrawal(s) confirmed.", changed);
                });
        });
}

internal static class WorkerLoop
{
    /// <summary>
    /// Runs <paramref name="action"/> only if the named distributed lock can be acquired immediately; otherwise
    /// skips quietly (another host instance holds it, or the lock backend is unavailable). The lock is a
    /// single-flight optimisation, never the correctness guard — that stays in the database (§7.4) — so skipping
    /// is always safe: the work is simply retried on the next tick.
    /// </summary>
    public static async Task SingleFlightAsync(
        IDistributedLockFactory lockFactory, string key, CancellationToken cancellationToken, Func<Task> action)
    {
        IAsyncDisposable handle;
        try
        {
            handle = await lockFactory.AcquireAsync(key, TimeSpan.Zero, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // contended or lock backend unavailable — skip this tick
        }

        await using (handle)
            await action();
    }

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
