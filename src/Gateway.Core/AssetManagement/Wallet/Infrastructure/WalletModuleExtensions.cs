using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddScoped<IDepositAddressProvisioner, WalletProvisioningService>();

        return services;
    }
}
