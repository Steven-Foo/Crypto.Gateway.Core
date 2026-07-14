using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Configuration;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure;

/// <summary>
/// The Withdrawal module's composition. It depends on capabilities the composition root also registers:
/// the Ledger's <c>IWithdrawalLedger</c> (synchronous reserve), the Blockchain
/// <c>ITransactionBuilder</c>/<c>ITransactionBroadcaster</c> + <c>IChainStatusReader</c>, KeyManagement's
/// <c>ISigner</c>, and Merchant's <c>IMerchantDirectory</c> — none reached into directly (§4.5).
/// </summary>
public static class WithdrawalModuleExtensions
{
    public static IServiceCollection AddWithdrawalModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<WithdrawalDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", WithdrawalDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IWithdrawalPolicyProvider>(_ => new ConfigurationWithdrawalPolicyProvider(configuration));
        services.AddSingleton<IHotWalletProvider>(_ => new ConfigurationHotWalletProvider(configuration));

        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<IWithdrawalApprovalService, WithdrawalApprovalService>();
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();

        return services;
    }
}
