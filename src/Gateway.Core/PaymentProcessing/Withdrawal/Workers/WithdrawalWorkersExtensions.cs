using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // The confirmation service needs GasAccountingOptions (5c). AddWithdrawalModule registers the
        // config-bound map (and, running first, wins in the host); this default keeps a workers-only composer
        // (e.g. a flow test) resolvable — an empty map means no gas journal, which is the safe default.
        services.TryAddSingleton(new GasAccountingOptions());

        services.AddSingleton(options);
        services.AddHostedService<WithdrawalProcessingWorker>();
        services.AddHostedService<WithdrawalConfirmationWorker>();
        return services;
    }
}
