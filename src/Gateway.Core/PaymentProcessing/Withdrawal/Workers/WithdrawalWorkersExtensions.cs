using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Workers;

public static class WithdrawalWorkersExtensions
{
    /// <summary>
    /// Registers <c>WithdrawalProcessingService</c>/<c>WithdrawalConfirmationService</c> plus the processing
    /// + confirmation workers. The host calls this after <c>AddWithdrawalModule</c> and after registering the
    /// signer, transaction builder/broadcaster, chain status reader, and ledger reserve — the two Application
    /// services need those ports, which is exactly why they live here and not in <c>AddWithdrawalModule</c>
    /// (a read-only composer like Ops never registers this).
    /// </summary>
    public static IServiceCollection AddWithdrawalWorkers(this IServiceCollection services, WithdrawalWorkerOptions options)
    {
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();

        services.AddSingleton(options);
        services.AddHostedService<WithdrawalProcessingWorker>();
        services.AddHostedService<WithdrawalConfirmationWorker>();
        return services;
    }
}
