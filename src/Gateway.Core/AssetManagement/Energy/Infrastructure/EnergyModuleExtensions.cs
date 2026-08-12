using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;

/// <summary>
/// Composition for the Energy module (Phase 5a — TRON-only resource monitoring). It owns policy in SQL
/// Server (schema <c>energy</c>) and observability snapshots in MongoDB. It depends on
/// <c>IPlatformWalletDirectory</c> (Wallet) and <c>IAccountResourceReader</c> (Blockchain) via their
/// Contracts — the host registers those (§4.5).
/// </summary>
public static class EnergyModuleExtensions
{
    public static IServiceCollection AddEnergyModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<EnergyDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", EnergyDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IEnergyPolicyRepository, EnergyPolicyRepository>();
        services.AddScoped<ResourceMonitorService>();

        // 5b — stake/delegate. Options + repo + the read/create-only services (no signer), so
        // IEnergyDelegationService is always-on for Sweep to consume (§4.5). The signing processing/
        // confirmation services live behind AddEnergyWorkers (they need ISigner/broadcaster, §4.7/§10).
        services.TryAddSingleton(ReadOperationOptions(configuration));
        services.AddScoped<IEnergyOperationRepository, EnergyOperationRepository>();
        services.AddScoped<StakingWalletLocator>();
        services.AddScoped<StakingService>();
        services.AddScoped<IEnergyDelegationService, EnergyDelegationService>();

        services.AddEnergyMongo(configuration);
        return services;
    }

    private static EnergyOperationOptions ReadOperationOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Energy:Operations");
        BigInteger ParseOr(string key, BigInteger fallback) =>
            string.IsNullOrWhiteSpace(section[key]) ? fallback : BigInteger.Parse(section[key]!, CultureInfo.InvariantCulture);

        var defaults = new EnergyOperationOptions();
        return new EnergyOperationOptions
        {
            RequiredEnergyPerTransfer = ParseOr("RequiredEnergyPerTransfer", defaults.RequiredEnergyPerTransfer),
            DelegateTrxSun = ParseOr("DelegateTrxSun", defaults.DelegateTrxSun),
            StakeIncrementTrxSun = ParseOr("StakeIncrementTrxSun", defaults.StakeIncrementTrxSun),
            Confirmations = string.IsNullOrWhiteSpace(section["Confirmations"])
                ? defaults.Confirmations
                : int.Parse(section["Confirmations"]!, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The codebase's first MongoDB wiring: resource snapshots + history (derived, never money truth — §2).
    /// <see cref="MongoClient"/> is lazy, pooled, and thread-safe, so a single instance serves the app and the
    /// host boots even when Mongo is down — the monitor worker logs and retries on the next poll.
    /// </summary>
    private static IServiceCollection AddEnergyMongo(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Missing configuration 'Mongo:ConnectionString'.");
        var databaseName = configuration["Mongo:Database"] ?? "CryptoPaymentEngine";

        services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connection));
        services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddSingleton<IWalletResourceStore, MongoWalletResourceStore>();
        services.AddSingleton<IResourceHistoryStore, MongoResourceHistoryStore>();
        return services;
    }
}
