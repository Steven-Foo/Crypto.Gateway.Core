using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure;

/// <summary>
/// The PaymentIntent module's composition. It owns the deposit-invoice lifecycle, the merchant address pool
/// (reuse-or-mint), and matching confirmed deposits to invoices. It depends on Wallet's provisioning
/// capability (<c>IDepositAddressProvisioner</c>), which the composition root must also register (§4.5).
/// </summary>
public static class PaymentIntentModuleExtensions
{
    public static IServiceCollection AddPaymentIntentModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<PaymentIntentDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", PaymentIntentDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<PaymentIntentOptions>(configuration.GetSection(PaymentIntentOptions.SectionName));

        services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();
        services.AddScoped<IPaymentIntentService, PaymentIntentService>();
        services.AddScoped<IPaymentIntentDirectory, PaymentIntentDirectory>();

        // Consumes Deposit's confirmation event to match invoices (§7.5). Coexists with the Ledger's own
        // handler for the same event — both fire; each owns a separate transaction.
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, PaymentIntentMatchHandler>();

        return services;
    }
}
