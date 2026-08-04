using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

public static class TreasuryModuleExtensions
{
    /// <summary>
    /// Registers the Treasury module. Persistence-less: it composes the Wallet and KeyManagement modules'
    /// Contracts, so the host must register those too. Always-on — the read directory returns a failure
    /// Result when no hot wallet is registered, so this is safe even before any wallet exists.
    /// </summary>
    public static IServiceCollection AddTreasuryModule(this IServiceCollection services)
    {
        services.AddScoped<ITreasuryHotWalletDirectory, TreasuryHotWalletDirectory>();
        services.AddScoped<TreasuryHotWalletProvisioningService>();
        return services;
    }

    /// <summary>
    /// DEV/TESTNET-tier ONLY. Binds <c>Treasury:DevHotWallets</c> and registers the boot-time seeder that
    /// idempotently registers the platform hot withdrawal wallet(s). Call this only in the testnet tier,
    /// alongside <c>AddDevelopmentKeyCustody</c> (whose in-memory secret provider holds the referenced key).
    /// Never in production — a production hot wallet is registered through an ops action backed by a KMS (§10).
    /// </summary>
    public static IServiceCollection AddDevelopmentTreasuryHotWalletSeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TreasuryDevHotWalletOptions>(
            configuration.GetSection(TreasuryDevHotWalletOptions.SectionName));
        services.AddHostedService<TreasuryHotWalletSeeder>();
        return services;
    }
}
