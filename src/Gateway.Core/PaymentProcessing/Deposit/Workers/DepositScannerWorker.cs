using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;

/// <summary>
/// Polls each configured chain for new deposits (§9). A new DI scope per pass gives it a fresh scoped
/// DbContext. Every pass is idempotent and resumable via the scan cursor, so a crash mid-loop is safe.
/// One chain's failure is logged and never stops the others or the loop.
/// </summary>
public sealed class DepositScannerWorker(
    IServiceScopeFactory scopeFactory,
    DepositWorkerOptions options,
    ILogger<DepositScannerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ScanInterval);

        do
        {
            foreach (var chain in options.Chains)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var detection = scope.ServiceProvider.GetRequiredService<DepositDetectionService>();
                    var recorded = await detection.ScanOnceAsync(chain, stoppingToken);
                    if (recorded > 0)
                        logger.LogInformation("Recorded {Count} new deposit(s) on {Chain}.", recorded, chain);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Deposit scan failed for {Chain}; will retry next tick.", chain);
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
