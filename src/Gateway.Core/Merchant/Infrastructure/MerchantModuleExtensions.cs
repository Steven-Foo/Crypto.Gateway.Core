using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;

/// <summary>
/// The Merchant module's composition. A host calls this; the host itself contains no merchant
/// logic (§4.7).
/// </summary>
public static class MerchantModuleExtensions
{
    public static IServiceCollection AddMerchantModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<MerchantDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", MerchantDbContext.SchemaName))
            .UseBigIntegerMoney());

        services.Configure<ApiCredentialOptions>(configuration.GetSection(ApiCredentialOptions.SectionName));

        // TryAdd: a host (or a test) may supply a fake clock; the module must not stomp on it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IApiSecretHasher, HmacApiSecretHasher>();
        services.AddSingleton<IApiCredentialGenerator, ApiCredentialGenerator>();
        services.AddScoped<IMerchantRepository, MerchantRepository>();
        services.AddScoped<IMerchantDirectory, MerchantDirectory>();
        services.AddScoped<IMerchantRegistrar, MerchantRegistrar>();
        services.AddScoped<IMerchantAuthenticator, MerchantAuthenticator>();

        return services;
    }
}
