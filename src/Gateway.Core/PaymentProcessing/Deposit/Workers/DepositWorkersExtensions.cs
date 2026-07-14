using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;

public static class DepositWorkersExtensions
{
    /// <summary>
    /// Registers the deposit scanner and confirmation background workers. The host calls this after
    /// <c>AddDepositModule</c> and after registering a chain source (in-memory or JSON-RPC) and the
    /// wallet directory.
    /// </summary>
    public static IServiceCollection AddDepositWorkers(this IServiceCollection services, DepositWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService<DepositScannerWorker>();
        services.AddHostedService<DepositConfirmationWorker>();
        return services;
    }
}
