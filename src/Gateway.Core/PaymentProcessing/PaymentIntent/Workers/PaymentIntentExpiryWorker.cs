using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Workers;

/// <summary>
/// Periodically flips lapsed Waiting invoices to Expired, releasing their addresses back to the pool (§9).
/// Idempotent and resumable — each pass processes a fresh batch in a new scope; a crash mid-sweep just
/// re-runs next tick. Purely housekeeping: it moves no money and blocks nothing if it falls behind.
/// </summary>
public sealed class PaymentIntentExpiryWorker(
    IServiceScopeFactory scopeFactory,
    PaymentIntentWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<PaymentIntentExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ExpirySweepInterval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IPaymentIntentRepository>();
                var expired = await repository.ExpireStaleAsync(timeProvider.GetUtcNow(), options.ExpiryBatchSize, stoppingToken);
                if (expired > 0)
                    logger.LogInformation("Expired {Count} lapsed payment intent(s).", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment intent expiry sweep failed; will retry next tick.");
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
