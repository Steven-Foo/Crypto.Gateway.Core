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
}
