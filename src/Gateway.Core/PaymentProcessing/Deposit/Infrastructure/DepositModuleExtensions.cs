using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Configuration;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure;

/// <summary>
/// The Deposit module's composition. It owns detection, confirmation, dedup, and the scan cursor.
/// It depends on two capabilities the composition root must also register, so this module never binds
/// a concrete chain provider or reaches into another module (§4.5, §8):
/// <list type="bullet">
///   <item>the read-only chain source — <c>IDepositScanner</c>/<c>IChainStatusReader</c> (Blockchain;
///   dev/test via <c>AddInMemoryChainSource()</c>, prod via the JSON-RPC adapters);</item>
///   <item>the wallet directory — <c>IWalletDirectory</c> (Wallet), to answer whose address a transfer hit.</item>
/// </list>
/// </summary>
public static class DepositModuleExtensions
{
    public static IServiceCollection AddDepositModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<DepositDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", DepositDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);

        // Per-chain policy is built once from configuration; a missing policy fails loud, never credits by default.
        services.AddSingleton<IDepositPolicyProvider>(_ => new ConfigurationDepositPolicyProvider(configuration));

        services.AddScoped<IDepositRepository, DepositRepository>();
        services.AddScoped<IScanCursorStore, ScanCursorStore>();

        services.AddScoped<DepositDetectionService>();
        services.AddScoped<DepositConfirmationService>();

        return services;
    }
}
