using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Workers;

public static class ReconciliationWorkersExtensions
{
    /// <summary>
    /// Registers the reconciliation background worker. The host calls this after <c>AddReconciliationModule</c>,
    /// once the Ledger, Wallet, and Blockchain read capabilities it consumes are registered (an
    /// <c>IBalanceReader</c> — in-memory or a real TRON adapter — plus the Wallet and Ledger modules).
    /// </summary>
    public static IServiceCollection AddReconciliationWorkers(this IServiceCollection services, ReconciliationWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService<ReconciliationWorker>();
        return services;
    }
}
