using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Configuration;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure;

/// <summary>
/// The Deposit module's read-only/repository composition: dedup, scan cursor, and <c>IDepositLookup</c> —
/// safe for any host to compose (e.g. Ops, for its transaction search). The heavier detection/confirmation
/// services (which need the chain-source ports <c>IDepositScanner</c>/<c>IChainStatusReader</c>, Blockchain)
/// live behind <c>AddDepositWorkers</c> instead, so a read-only composer never has to satisfy a chain
/// adapter it will never use (§4.5, §4.7, §8).
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
        services.AddScoped<IDepositLookup, DepositLookup>();

        // DepositDetectionService/DepositConfirmationService are NOT registered here — they need the
        // chain-source ports (IDepositScanner/IChainStatusReader), which only a host running the scanner
        // actually registers. They live behind AddDepositWorkers instead, so a read-only composer (Ops,
        // via IDepositLookup) never has to satisfy a chain adapter it will never use (§4.7).

        return services;
    }
}
