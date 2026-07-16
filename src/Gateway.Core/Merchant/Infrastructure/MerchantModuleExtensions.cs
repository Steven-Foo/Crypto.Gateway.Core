using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Seeding;
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
        services.Configure<SigningSecretOptions>(configuration.GetSection(SigningSecretOptions.SectionName));

        // TryAdd: a host (or a test) may supply a fake clock; the module must not stomp on it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IApiSecretHasher, HmacApiSecretHasher>();
        services.AddSingleton<ISecretCipher, AesGcmSecretCipher>();
        services.AddSingleton<IApiCredentialGenerator, ApiCredentialGenerator>();
        services.AddScoped<IMerchantRepository, MerchantRepository>();
        services.AddScoped<IMerchantDirectory, MerchantDirectory>();
        services.AddScoped<IMerchantFeeSchedule, MerchantFeeSchedule>();
        services.AddScoped<IMerchantRegistrar, MerchantRegistrar>();
        services.AddScoped<IMerchantAuthenticator, MerchantAuthenticator>();

        // Request-signing (§10): verify inbound gateway signatures / sign outbound callbacks without the
        // signing secret ever leaving this module.
        services.AddScoped<IMerchantRequestVerifier, MerchantRequestVerifier>();
        services.AddScoped<IMerchantCallbackSigner, MerchantCallbackSigner>();

        return services;
    }

    /// <summary>
    /// DEVELOPMENT / LOCAL ONLY. Registers the idempotent dev merchant seeder (section <c>Merchant:DevSeed</c>),
    /// so a signed <c>/api/v1</c> request works on a fresh clone with fixed, documented credentials. NEVER call
    /// this outside the Development branch: it activates a merchant with a config-known signing secret (§10).
    /// </summary>
    public static IServiceCollection AddDevelopmentMerchantSeed(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DevMerchantSeedOptions>(configuration.GetSection(DevMerchantSeedOptions.SectionName));
        services.AddHostedService<DevMerchantSeeder>();
        return services;
    }
}
