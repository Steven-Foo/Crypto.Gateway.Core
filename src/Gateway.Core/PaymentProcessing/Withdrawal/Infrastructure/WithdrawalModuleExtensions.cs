using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Configuration;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure;

/// <summary>
/// The Withdrawal module's request/approval/repository composition — safe for any host to compose (e.g.
/// Ops, for its transaction search via <c>IWithdrawalDirectory</c>). It depends on the Ledger's
/// <c>IWithdrawalLedger</c> (synchronous reserve) and Merchant's <c>IMerchantDirectory</c>/fee schedule, none
/// reached into directly (§4.5). The heavier processing/confirmation services (which need
/// <c>ITransactionBuilder</c>/<c>ITransactionBroadcaster</c>/<c>IChainStatusReader</c>/<c>ISigner</c>) live
/// behind <c>AddWithdrawalWorkers</c> instead, so a read-only composer never has to satisfy a signer or
/// broadcaster it will never use (§10).
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

        // The hot wallet now comes from the Treasury module's DB-backed registration, not raw config.
        // Scoped because it reads scoped DbContext-backed Contracts; the processing service already runs
        // inside a per-pass DI scope. The host must register the Treasury module (AddTreasuryModule).
        services.AddScoped<IHotWalletProvider, TreasuryHotWalletProvider>();

        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalDirectory, WithdrawalDirectory>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<IWithdrawalApprovalService, WithdrawalApprovalService>();

        // WithdrawalProcessingService/WithdrawalConfirmationService are NOT registered here — they need the
        // chain-processing ports (ITransactionBuilder/ISigner/ITransactionBroadcaster/IHotWalletProvider/
        // IChainStatusReader), which only a host running the processing workers actually registers. They
        // live behind AddWithdrawalWorkers instead, so a read-only composer (Ops, via IWithdrawalDirectory)
        // never has to satisfy a signer/broadcaster it will never use (§4.7, §10).

        return services;
    }
}
