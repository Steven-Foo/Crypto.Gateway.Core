using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure.Persistence;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure;

/// <summary>
/// The Notification module's composition: outbound merchant callbacks. Consumes <see cref="PaymentIntentMatched"/>/
/// <see cref="PaymentIntentFailed"/> and <see cref="WithdrawalConfirmed"/>/<see cref="WithdrawalFailed"/>
/// (delivered via each module's own outbox by the host's dispatcher, §7.5), builds+signs the callback, and
/// schedules it (<see cref="ICallbackDeliveryScheduler"/>). Owns its own delivery-tracking store
/// (<see cref="CallbackDeliveryRepository"/>) — Ops reads it via <see cref="ICallbackDeliveryQuery"/>, never
/// by reaching into PaymentIntent/Withdrawal's own tables (§4.5). Actual sending — with a bounded retry
/// schedule, not this event's own delivery — is <c>CallbackDeliveryProcessingService</c>'s job, registered
/// by <c>AddNotificationWorkers</c> (Workers project) alongside <c>CallbackDeliveryWorker</c>, the same split
/// Deposit/Withdrawal use for their chain-dependent processing services.
/// </summary>
public static class NotificationModuleExtensions
{
    public static IServiceCollection AddNotificationModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<NotificationDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", NotificationDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CallbackDeliveryRepository>();
        services.AddScoped<ICallbackDeliveryScheduler>(sp => sp.GetRequiredService<CallbackDeliveryRepository>());
        services.AddScoped<ICallbackDeliveryRepository>(sp => sp.GetRequiredService<CallbackDeliveryRepository>());
        services.AddScoped<ICallbackDeliveryQuery, CallbackDeliveryQuery>();
        services.AddScoped<ICallbackDeliveryResendService, CallbackDeliveryResendService>();

        services.AddHttpClient(HttpWebhookSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IWebhookSender, HttpWebhookSender>();
        services.AddScoped<IIntegrationEventHandler<PaymentIntentMatched>, DepositCallbackHandler>();
        services.AddScoped<IIntegrationEventHandler<PaymentIntentFailed>, PaymentIntentFailedCallbackHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedCallbackHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedCallbackHandler>();
        return services;
    }
}
