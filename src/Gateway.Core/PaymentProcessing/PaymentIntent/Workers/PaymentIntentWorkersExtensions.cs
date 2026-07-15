using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Workers;

public static class PaymentIntentWorkersExtensions
{
    /// <summary>Registers the expiry sweep. The host supplies cadence/batch overrides; defaults suit dev.</summary>
    public static IServiceCollection AddPaymentIntentWorkers(
        this IServiceCollection services, PaymentIntentWorkerOptions? options = null)
    {
        services.AddSingleton(options ?? new PaymentIntentWorkerOptions());
        services.AddHostedService<PaymentIntentExpiryWorker>();
        return services;
    }
}
