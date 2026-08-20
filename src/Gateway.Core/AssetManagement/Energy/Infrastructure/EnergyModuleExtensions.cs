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

    /// <summary>
    /// READ-ONLY composition for a host that only surfaces energy state (the ops read API), not the monitor/
    /// stake/delegate path. Registers the SQL DbContext + the no-tracking <see cref="IEnergyOperationDirectory"/>
    /// and the Mongo resource-snapshot store (<see cref="IWalletResourceStore"/> for the resource-health read) —
    /// but NOT <c>ResourceMonitorService</c>/<c>StakingService</c>/<c>EnergyDelegationService</c> or the workers,
    /// which need <c>IAccountResourceReader</c>/<c>IResourceOperationBuilder</c>/<c>ISigner</c> the ops host lacks
    /// and must never run (§4.7). Never moves funds, never signs, never writes a snapshot (§2).
    /// </summary>
    public static IServiceCollection AddEnergyReadModel(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<EnergyDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", EnergyDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.AddScoped<IEnergyOperationDirectory, EnergyOperationDirectory>();
        services.AddEnergyMongo(configuration); // resource snapshots (read-only use here)
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
            RequiredBandwidthPerTransfer = ParseOr("RequiredBandwidthPerTransfer", defaults.RequiredBandwidthPerTransfer),
            MinTrxCushionSun = ParseOr("MinTrxCushionSun", defaults.MinTrxCushionSun),
            DelegateTrxSun = ParseOr("DelegateTrxSun", defaults.DelegateTrxSun),
            TopUpTrxSun = ParseOr("TopUpTrxSun", defaults.TopUpTrxSun),
            StakeIncrementTrxSun = ParseOr("StakeIncrementTrxSun", defaults.StakeIncrementTrxSun),
            Confirmations = string.IsNullOrWhiteSpace(section["Confirmations"])
                ? defaults.Confirmations
                : int.Parse(section["Confirmations"]!, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// DEV/TESTNET-tier ONLY. Idempotently registers the platform <b>staking (energy) wallet</b> from
    /// <c>Energy:DevStakingWallets</c> — its imported signing key (KeyManagement, purpose Energy), its
    /// <c>WalletType.Energy</c> Wallet row (so <c>StakingWalletLocator</c> finds it), and an <c>EnergyPolicy</c>
    /// so auto-stake has thresholds. This is the delegation SOURCE the sweep energy gate draws from. Call only
    /// in the testnet tier, alongside <c>AddDevelopmentKeyCustody</c> (whose imported-key registrar it uses).
    /// Never in production — the staking wallet is provisioned through a KMS-backed ops action, not config (§10).
    /// A live delegation still needs the wallet funded with (testnet) TRX to freeze.
    /// </summary>
    public static IServiceCollection AddDevelopmentEnergyStakingSeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EnergyStakingDevOptions>(configuration.GetSection(EnergyStakingDevOptions.SectionName));
        services.AddHostedService<EnergyStakingWalletSeeder>();
        return services;
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
