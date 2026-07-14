using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Workers;

public static class WithdrawalWorkersExtensions
{
    /// <summary>
    /// Registers the withdrawal processing + confirmation workers. The host calls this after
    /// <c>AddWithdrawalModule</c> and after registering the signer, transaction builder/broadcaster,
    /// chain status reader, and ledger reserve.
    /// </summary>
    public static IServiceCollection AddWithdrawalWorkers(this IServiceCollection services, WithdrawalWorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService<WithdrawalProcessingWorker>();
        services.AddHostedService<WithdrawalConfirmationWorker>();
        return services;
    }
}
