using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

public static class TreasuryModuleExtensions
{
    /// <summary>
    /// Registers the Treasury module: the hot-pool directory/provisioning (composed over Wallet + KeyManagement
    /// Contracts — the host must register those) plus Treasury's own persistence for the watch-only cold treasury
    /// collection wallets. Nothing in this module builds, signs or broadcasts a transaction.
    /// </summary>
    public static IServiceCollection AddTreasuryModule(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<TreasuryDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", TreasuryDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);

        // Hot pool (existing).
        services.AddScoped<ITreasuryHotWalletDirectory, TreasuryHotWalletDirectory>();
        services.AddScoped<TreasuryHotWalletProvisioningService>();

        // Cold collection wallets: watch-only, the destinations Sweep concentrates deposits into — one per
        // (chain, kind), clean sweeps to the Safe wallet and flagged ones to the Danger wallet.
        services.Configure<TreasuryScreeningOptions>(configuration.GetSection(TreasuryScreeningOptions.SectionName));
        services.AddScoped<ITreasuryColdWalletRepository, TreasuryColdWalletRepository>();
        services.AddScoped<ITreasuryColdWalletDirectory, TreasuryColdWalletDirectory>();
        services.AddScoped<ITreasuryColdWalletRegistrar, TreasuryColdWalletRegistrationService>();

        return services;
    }

    /// <summary>
    /// DEV/TESTNET-tier ONLY. Binds <c>Treasury:HotWalletPool</c> and registers the boot-time seeder that
    /// idempotently derives+registers the platform hot withdrawal pool (children of the one withdrawal HD
    /// wallet). Call this only in the testnet tier, alongside <c>AddDevelopmentKeyCustody</c> (whose in-memory
    /// provisioner mints the withdrawal seed). Never in production — the pool is provisioned through an ops
    /// action backed by a KMS, not seeded from config (§10).
    /// </summary>
    public static IServiceCollection AddDevelopmentTreasuryHotWalletSeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TreasuryDevHotWalletOptions>(
            configuration.GetSection(TreasuryDevHotWalletOptions.SectionName));
        services.AddHostedService<TreasuryHotWalletSeeder>();
        return services;
    }

    /// <summary>
    /// DEV/TESTNET-tier ONLY. Registers the boot-time seeder that idempotently registers the cold treasury
    /// address(es) from <c>Treasury:ColdWallets</c> (a public, watch-only address — no key, §10). In production
    /// the cold address is registered through the staff ops action, not seeded from config.
    /// </summary>
    public static IServiceCollection AddDevelopmentTreasuryColdWalletSeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TreasuryDevColdWalletOptions>(
            configuration.GetSection(TreasuryDevColdWalletOptions.SectionName));
        services.AddHostedService<TreasuryColdWalletSeeder>();
        return services;
    }
}
