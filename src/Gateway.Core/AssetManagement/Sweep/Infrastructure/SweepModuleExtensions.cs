using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Configuration;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure;

/// <summary>
/// Composition for the Sweep module (concentrate deposit balances into the hot wallet). It owns SQL schema
/// <c>sweep</c> (the sweep state machine) and depends, through Contracts only (§4.5), on Wallet
/// (<c>IWalletDirectory</c>), Blockchain (<c>IAssetCatalog</c>/<c>IBalanceReader</c>/<c>ITransactionBuilder</c>/
/// <c>ITransactionBroadcaster</c>/<c>IChainStatusReader</c>), KeyManagement (<c>IDepositSigningKeyDirectory</c>/
/// <c>ISigner</c>), and Treasury (<c>ITreasuryHotWalletDirectory</c>). The heavier scan/processing/confirmation
/// services + workers live behind <c>AddSweepWorkers</c>, so a composer that only needs the repository never
/// has to satisfy a signer/broadcaster (§4.7, §10).
/// </summary>
public static class SweepModuleExtensions
{
    public static IServiceCollection AddSweepModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<SweepDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SweepDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<SweepScreeningOptions>(configuration.GetSection(SweepScreeningOptions.SectionName));

        // Configuration supplies the defaults; the effective dials are a per-chain row an operator can
        // change while the platform runs. One scoped service serves both the workers (ISweepPolicyProvider)
        // and the back office (ISweepSettingsService), so a settings screen can never disagree with the scan.
        services.AddSingleton<ISweepConfigurationDefaults>(_ => new ConfigurationSweepDefaults(configuration));
        services.AddScoped<ISweepSettingsRepository, SweepSettingsRepository>();
        services.AddScoped<SweepSettingsService>();
        services.AddScoped<ISweepPolicyProvider>(sp => sp.GetRequiredService<SweepSettingsService>());
        services.AddScoped<ISweepSettingsService>(sp => sp.GetRequiredService<SweepSettingsService>());
        services.AddScoped<ISweepRepository, SweepRepository>();

        return services;
    }

    /// <summary>
    /// READ-ONLY composition for a host that only surfaces sweep state (the ops read API), not the scan/sign/
    /// broadcast path. Registers the DbContext + the no-tracking <see cref="ISweepDirectory"/> — but NOT the
    /// policy provider, the mutating repository, or the workers, so a host that lacks the signer/broadcaster/
    /// balance-reader the workers need can still read what the money host wrote (§4.7). Never moves funds.
    /// </summary>
    public static IServiceCollection AddSweepReadModel(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<SweepDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SweepDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISweepDirectory, SweepDirectory>();

        // The settings service, so staff can read and re-tune the dials and ask for an out-of-schedule pass.
        // It writes only the settings row — never a sweep — so this host still scans, signs and broadcasts
        // nothing (§4.7); a manual trigger is a request the money host picks up on its next tick.
        services.AddSingleton<ISweepConfigurationDefaults>(_ => new ConfigurationSweepDefaults(configuration));
        services.AddScoped<ISweepSettingsRepository, SweepSettingsRepository>();
        services.AddScoped<SweepSettingsService>();
        services.AddScoped<ISweepSettingsService>(sp => sp.GetRequiredService<SweepSettingsService>());
        return services;
    }
}
