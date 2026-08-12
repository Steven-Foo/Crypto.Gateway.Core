using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Workers;

/// <summary>
/// The reconciliation audit backstop (§9): every <see cref="ReconciliationWorkerOptions.ReconcileInterval"/>,
/// compares the ledger's custody holding against the summed on-chain balance for each chain and records the
/// result. A fresh DI scope per pass gives it clean scoped reads (Ledger/Wallet directories); each pass is a
/// pure read-and-record and so is inherently idempotent and resumable. One chain's failure is logged and
/// never stops the others or the loop. It moves no money and holds no keys (§5, §10).
/// </summary>
public sealed class ReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    ReconciliationWorkerOptions options,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReconcileInterval);

        do
        {
            foreach (var chain in options.Chains)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
                    await service.ReconcileAsync(chain, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Reconciliation pass failed for {Chain}; will retry next tick.", chain);
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
