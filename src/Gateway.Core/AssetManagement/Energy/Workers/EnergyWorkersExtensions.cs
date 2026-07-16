using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

public static class EnergyWorkersExtensions
{
    /// <summary>
    /// Registers the resource monitor background worker. The host calls this after <c>AddEnergyModule</c>,
    /// after registering an <c>IAccountResourceReader</c> (in-memory or a real TRON adapter) and the Wallet module.
    /// </summary>
    public static IServiceCollection AddEnergyWorkers(this IServiceCollection services, EnergyWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService<ResourceMonitorWorker>();
        return services;
    }
}
