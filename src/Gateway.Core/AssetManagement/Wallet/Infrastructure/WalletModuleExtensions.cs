using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;

public static class WalletModuleExtensions
{
    /// <summary>
    /// Registers the Wallet module. It depends on <c>IWalletDerivation</c> (KeyManagement) and
    /// <c>IMerchantDirectory</c> (Merchant) via their Contracts — the host registers those modules too.
    /// </summary>
    public static IServiceCollection AddWalletModule(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<WalletDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", WalletDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<IWalletDirectory, WalletDirectory>();
        services.AddScoped<IPlatformWalletDirectory, PlatformWalletDirectory>();
        services.AddScoped<IPlatformWalletRegistrar, PlatformWalletRegistrationService>();
        services.AddScoped<IDepositAddressProvisioner, WalletProvisioningService>();
        services.AddScoped<IWalletAdminService, WalletAdminService>();

        // Requires IConnectionMultiplexer (AddRedisInfrastructure) — resolved lazily, so this registration
        // is harmless in a host that never actually reserves a wallet (DI only constructs it on first use).
        services.AddScoped<IWalletReservationLock, RedisWalletReservationLock>();

        // Bumps DepositsReceivedCount on the wallet a confirmed deposit landed on — only fires wherever the
        // Deposit module's outbox is actually dispatched (today: the MerchantGateway host); harmless, unused
        // registration in a host that doesn't compose Deposit (e.g. OperationsApi).
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, WalletDepositActivityHandler>();

        return services;
    }

    /// <summary>
    /// Registers screening of the platform's OWN funded deposit addresses — the only inbound control that
    /// is actually available, since a transfer cannot be screened in flight and an arrived deposit is never
    /// refused. Records and flags; it changes nothing about the money.
    ///
    /// <para>Deliberately separate from <see cref="AddWalletModule"/> and from the worker below. A host that
    /// only offers the MANUAL sweep (the ops host) registers this and not the worker, because an ops host
    /// runs no background work (§4.7).</para>
    /// </summary>
    public static IServiceCollection AddDepositAddressScreening(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DepositAddressScreeningOptions>(
            configuration.GetSection(DepositAddressScreeningOptions.SectionName));

        // Soft resolution, like every other screening consumer: a host that composes Wallet but not
        // Compliance must still boot. Enabled-but-not-composed reports an error rather than quietly
        // checking nothing.
        services.AddScoped(sp => new DepositAddressScreeningService(
            sp.GetRequiredService<IWalletDirectory>(),
            sp.GetService<IAddressScreeningService>(),
            sp.GetRequiredService<IOptions<DepositAddressScreeningOptions>>(),
            sp.GetRequiredService<ILogger<DepositAddressScreeningService>>()));

        return services;
    }
}
