using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Workers;

public static class NotificationWorkersExtensions
{
    /// <summary>
    /// Registers <c>CallbackDeliveryProcessingService</c> plus the delivery worker. The host calls this after
    /// <c>AddNotificationModule</c> — this is the only place actual callback sending happens; a read-only
    /// composer (Ops) never calls this and so never sends anything, only ever reads via
    /// <c>ICallbackDeliveryQuery</c>/<c>ICallbackDeliveryResendService</c>.
    /// </summary>
    public static IServiceCollection AddNotificationWorkers(this IServiceCollection services, CallbackDeliveryWorkerOptions options)
    {
        services.AddScoped<CallbackDeliveryProcessingService>();

        services.AddSingleton(options);
        services.AddHostedService<CallbackDeliveryWorker>();
        return services;
    }
}
