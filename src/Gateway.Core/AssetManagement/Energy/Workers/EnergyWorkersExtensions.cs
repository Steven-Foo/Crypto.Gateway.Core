using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

public static class EnergyWorkersExtensions
{
    /// <summary>
    /// Registers the Energy background workers. The host calls this after <c>AddEnergyModule</c>, after
    /// registering an <c>IAccountResourceReader</c> (in-memory or a real TRON adapter) and the Wallet module.
    /// The 5b operation processing/confirmation services + workers need the chain-write ports
    /// (<c>IResourceOperationBuilder</c>/<c>ISigner</c>/<c>ITransactionBroadcaster</c>/<c>IChainStatusReader</c>),
    /// which only a host running the workers registers — so they live here, not in <c>AddEnergyModule</c>
    /// (§4.7, §10). Like withdrawal/sweep, the real signer is deferred: inert in prod until KMS lands.
    /// </summary>
    public static IServiceCollection AddEnergyWorkers(this IServiceCollection services, EnergyWorkerOptions options)
    {
        services.AddSingleton(options);

        // 5a
        services.AddHostedService<ResourceMonitorWorker>();

        // 5b — stake/delegate action phase.
        services.AddScoped<EnergyOperationProcessingService>();
        services.AddScoped<EnergyOperationConfirmationService>();
        services.AddHostedService<StakeReplenishWorker>();
        services.AddHostedService<EnergyOperationProcessingWorker>();
        services.AddHostedService<EnergyOperationConfirmationWorker>();
        return services;
    }
}
