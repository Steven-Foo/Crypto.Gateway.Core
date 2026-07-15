using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure;

/// <summary>
/// The Notification module's composition: outbound merchant callbacks. Consumes <see cref="PaymentIntentMatched"/>
/// (delivered via the PaymentIntent outbox by the host's dispatcher, §7.5) and posts a signed deposit callback.
/// </summary>
public static class NotificationModuleExtensions
{
    public static IServiceCollection AddNotificationModule(this IServiceCollection services)
    {
        services.AddHttpClient(HttpWebhookSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IWebhookSender, HttpWebhookSender>();
        services.AddScoped<IIntegrationEventHandler<PaymentIntentMatched>, DepositCallbackHandler>();
        return services;
    }
}
