using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;

public static class DepositWorkersExtensions
{
    /// <summary>
    /// Registers <c>DepositDetectionService</c>/<c>DepositConfirmationService</c> plus the scanner and
    /// confirmation background workers. The host calls this after <c>AddDepositModule</c> and after
    /// registering a chain source (in-memory or JSON-RPC) and the wallet directory — the two Application
    /// services need those chain-source ports, which is exactly why they live here and not in
    /// <c>AddDepositModule</c> (a read-only composer like Ops never registers this).
    /// </summary>
    public static IServiceCollection AddDepositWorkers(this IServiceCollection services, DepositWorkerOptions options)
    {
        services.AddScoped<DepositDetectionService>();
        services.AddScoped<DepositConfirmationService>();

        services.AddSingleton(options);
        services.AddHostedService<DepositScannerWorker>();
        services.AddHostedService<DepositConfirmationWorker>();
        return services;
    }
}
