using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Workers;

public static class SweepWorkersExtensions
{
    /// <summary>
    /// Registers the sweep scan/processing/confirmation services plus their workers. The host calls this after
    /// <c>AddSweepModule</c> and after registering the ports they need — <c>IBalanceReader</c>/
    /// <c>ITransactionBuilder</c>/<c>ITransactionBroadcaster</c>/<c>IChainStatusReader</c> (Blockchain),
    /// <c>ISigner</c>/<c>IDepositSigningKeyDirectory</c> (KeyManagement), <c>IWalletDirectory</c> (Wallet), and
    /// <c>ITreasuryHotWalletDirectory</c> (Treasury). They live here, not in <c>AddSweepModule</c>, so a
    /// read-only composer never has to satisfy a signer/broadcaster (§4.7, §10).
    /// </summary>
    public static IServiceCollection AddSweepWorkers(this IServiceCollection services, SweepWorkerOptions options)
    {
        services.AddScoped<SweepScanService>();
        services.AddScoped<SweepProcessingService>();
        services.AddScoped<SweepConfirmationService>();

        services.AddSingleton(options);
        services.AddHostedService<SweepScanWorker>();
        services.AddHostedService<SweepProcessingWorker>();
        services.AddHostedService<SweepConfirmationWorker>();
        return services;
    }
}
