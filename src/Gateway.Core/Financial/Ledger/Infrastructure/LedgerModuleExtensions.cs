using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure;

/// <summary>
/// The Ledger module's composition. Requires the host to have registered the shared Redis
/// infrastructure (<c>AddRedisInfrastructure</c>) — the posting store depends on
/// <c>IDistributedLockFactory</c> for its per-account single-flight lock.
/// </summary>
public static class LedgerModuleExtensions
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<LedgerDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", LedgerDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();

        // LedgerPoster is both the internal poster and the Withdrawal module's synchronous reserve contract.
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IWithdrawalLedger>(sp => sp.GetRequiredService<LedgerPoster>());

        // Consume Deposit's + Withdrawal's integration events (§7.5). The host wires these into the dispatch.
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, DepositConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<DepositOrphaned>, DepositOrphanedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedHandler>();

        return services;
    }
}
