using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Infrastructure.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Infrastructure;

/// <summary>
/// Composition for the Reconciliation module (first cut — ledger-vs-on-chain custody, log-only). It owns no
/// SQL schema: every fact it needs lives in the Ledger, Wallet, and Blockchain modules (read via their
/// Contracts, §4.5); it persists only observability snapshots to MongoDB (§2). The host must also register
/// the Ledger, Wallet, and Blockchain read capabilities (<c>ILedgerQuery</c>, <c>IPlatformWalletDirectory</c>/
/// <c>IWalletDirectory</c>, <c>IBalanceReader</c>/<c>IAssetCatalog</c>) that this module consumes.
/// </summary>
public static class ReconciliationModuleExtensions
{
    public static IServiceCollection AddReconciliationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ReadOptions(configuration));

        services.AddScoped<ReconciliationService>();
        services.AddReconciliationMongo(configuration);
        return services;
    }

    /// <summary>
    /// READ-ONLY composition for a host that only surfaces reconciliation snapshots (the ops read API), not the
    /// compute path. Registers the shared Mongo client/database + the snapshot store — but NOT
    /// <see cref="ReconciliationService"/> or the worker, so a host that lacks the ledger/on-chain read
    /// capabilities the compute needs (<c>ILedgerQuery</c>, <c>IBalanceReader</c>, the wallet directories) can
    /// still read what the money host's reconciliation worker wrote. Never writes a snapshot (§2 — derived,
    /// observability only).
    /// </summary>
    public static IServiceCollection AddReconciliationReadModel(this IServiceCollection services, IConfiguration configuration) =>
        services.AddReconciliationMongo(configuration);

    private static ReconciliationOptions ReadOptions(IConfiguration configuration)
    {
        var raw = configuration["Reconciliation:DriftTolerance"];
        var tolerance = string.IsNullOrWhiteSpace(raw) ? BigInteger.Zero : BigInteger.Parse(raw);
        return new ReconciliationOptions { DriftTolerance = tolerance };
    }

    /// <summary>
    /// Reuses the shared Mongo client/database (the same <see cref="MongoClient"/> Energy registers — it is
    /// lazy, pooled, and thread-safe, so <c>TryAdd</c> keeps a single instance) for the reconciliation snapshot
    /// + history collections (derived, never money truth — §2).
    /// </summary>
    private static IServiceCollection AddReconciliationMongo(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Missing configuration 'Mongo:ConnectionString'.");
        var databaseName = configuration["Mongo:Database"] ?? "CryptoPaymentEngine";

        services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connection));
        services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddSingleton<IReconciliationStore, MongoReconciliationStore>();
        services.AddSingleton<IReconciliationHistoryStore, MongoReconciliationHistoryStore>();
        return services;
    }
}
