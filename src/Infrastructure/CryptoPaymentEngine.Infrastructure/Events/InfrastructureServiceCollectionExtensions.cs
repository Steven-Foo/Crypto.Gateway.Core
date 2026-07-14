using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Infrastructure.Events;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddCryptoPaymentEngineEventBus(this IServiceCollection services) =>
        services.AddScoped<IEventBus, InProcessEventBus>();
}
