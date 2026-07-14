using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;

/// <summary>
/// Advances tracked deposits toward confirmation and detects reorgs (§9). Confirmations reaching the
/// policy threshold publish <c>DepositConfirmed</c> (via the outbox) → the Ledger credits; an orphaned
/// confirmed deposit publishes <c>DepositOrphaned</c> → the Ledger reverses. Idempotent per pass.
/// </summary>
public sealed class DepositConfirmationWorker(
    IServiceScopeFactory scopeFactory,
    DepositWorkerOptions options,
    ILogger<DepositConfirmationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ConfirmationInterval);

        do
        {
            foreach (var chain in options.Chains)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var confirmation = scope.ServiceProvider.GetRequiredService<DepositConfirmationService>();
                    var changed = await confirmation.TrackOnceAsync(chain, stoppingToken);
                    if (changed > 0)
                        logger.LogInformation("{Count} deposit(s) changed state on {Chain}.", changed, chain);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Deposit confirmation pass failed for {Chain}; will retry next tick.", chain);
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
