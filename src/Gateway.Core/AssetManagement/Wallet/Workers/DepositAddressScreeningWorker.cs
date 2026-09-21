using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Workers;

/// <summary>
/// Periodically screens the platform's own funded deposit addresses for contamination.
///
/// <para>This is the inbound control in the only form it can take. A transfer cannot be screened in flight,
/// and once it lands it is credited and never reversed — so what is watched is the other side of the graph:
/// our own receiving addresses, whose score rises when funds arrive from a bad counterparty.</para>
///
/// <para><b>The single-flight lock is load-bearing.</b> The provider's pacing gate is process-wide but not
/// cluster-wide, so two instances sweeping at once would each pace correctly while together breaching the
/// plan limit. A breach answers 429, and a 429 here would also consume the budget the payout gate depends
/// on — the control that actually holds money. This pass is the lowest-priority consumer of that quota and
/// must never be the reason a payout cannot be screened.</para>
/// </summary>
public sealed class DepositAddressScreeningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DepositAddressScreeningOptions> options,
    ILogger<DepositAddressScreeningWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Max(1, options.Value.IntervalHours);

        return WalletWorkerLoop.RunAsync(
            TimeSpan.FromHours(hours), stoppingToken, logger, "deposit address screening", async ct =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                await WalletWorkerLoop.SingleFlightAsync(
                    scope.ServiceProvider.GetRequiredService<IDistributedLockFactory>(),
                    "wallet:deposit-address-screening", ct, async () =>
                    {
                        var result = await scope.ServiceProvider
                            .GetRequiredService<DepositAddressScreeningService>()
                            .ScreenOnceAsync(force: false, ct);

                        if (result.Screened == 0)
                        {
                            return;
                        }

                        logger.LogInformation(
                            "Screened {Screened} deposit address(es); {Flagged} flagged.",
                            result.Screened, result.Flagged);

                        // Says plainly that the cap is biting. Without this the only symptom of a backlog
                        // that never clears is addresses quietly going unscreened for longer and longer.
                        if (result.Candidates > result.Screened)
                        {
                            logger.LogInformation(
                                "{Remaining} address(es) were due but deferred by the per-pass cap; "
                                + "they are picked up next pass.",
                                result.Candidates - result.Screened);
                        }
                    });
            });
    }
}

/// <summary>
/// The loop-and-single-flight helper, as carried by the Withdrawal, Notification and Merchant workers.
/// Duplicated rather than shared because it is internal to each module by design (§4.5) — a module has to be
/// readable, and extractable, without reaching into another one for a dozen lines of scheduling.
/// </summary>
internal static class WalletWorkerLoop
{
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
