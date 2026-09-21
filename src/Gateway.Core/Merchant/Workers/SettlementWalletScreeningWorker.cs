using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Workers;

/// <summary>
/// Periodically re-screens whitelisted settlement wallets, so a verdict taken at whitelisting time does not
/// stand unchallenged forever.
///
/// <para><b>The single-flight lock is load-bearing, not an optimisation.</b> The provider's pacing gate is
/// in-process. Two instances would each pace correctly against their own gate while together breaching the
/// plan's per-second limit — and a breach answers 429, which is a screening with no verdict. If this ever
/// runs concurrently across instances, the limiter has to become a Redis token bucket first.</para>
///
/// <para><b>Registered unconditionally, gated inside.</b> The service checks its own configuration flag
/// rather than the host deciding whether to register the worker, so turning the feature on or off is a
/// configuration change and not a redeploy, and so an operator reading the worker list sees the same set
/// everywhere.</para>
/// </summary>
public sealed class SettlementWalletScreeningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MerchantScreeningOptions> options,
    ILogger<SettlementWalletScreeningWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Max(1, options.Value.RescreenIntervalHours);

        return WorkerLoop.RunAsync(
            TimeSpan.FromHours(hours), stoppingToken, logger, "settlement wallet screening", async ct =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                await WorkerLoop.SingleFlightAsync(
                    scope.ServiceProvider.GetRequiredService<IDistributedLockFactory>(),
                    "merchant:settlement-screening", ct, async () =>
                    {
                        var result = await scope.ServiceProvider
                            .GetRequiredService<SettlementWalletScreeningService>()
                            .RescreenOnceAsync(ct);

                        // Only worth a line when something actually cost a call. A pass that found every
                        // verdict still fresh did nothing and should not fill the log saying so.
                        if (result.Screened > 0)
                        {
                            logger.LogInformation(
                                "Re-screened {Screened} of {Total} settlement wallet(s); {Flagged} worsened.",
                                result.Screened, result.Total, result.Flagged);
                        }
                    });
            });
    }
}

/// <summary>
/// The same loop-and-single-flight helper the Withdrawal and Notification workers carry. Duplicated rather
/// than shared because it is internal to each module by design (§4.5) — a module must be understandable,
/// and extractable, without reaching into another one for a dozen lines of scheduling.
/// </summary>
internal static class WorkerLoop
{
    /// <summary>
    /// Runs <paramref name="action"/> only if the named distributed lock can be acquired immediately;
    /// otherwise skips quietly. Skipping is always safe here: nothing is lost, the pass simply runs on the
    /// next tick.
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
            return; // contended, or the lock backend is unavailable — skip this tick
        }

        await using (handle)
            await action();
    }

    public static async Task RunAsync(
        TimeSpan interval, CancellationToken stoppingToken, ILogger logger, string name,
        Func<CancellationToken, Task> pass)
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
