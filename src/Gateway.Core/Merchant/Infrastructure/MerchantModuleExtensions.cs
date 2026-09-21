using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Seeding;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // Platform-default fee for unpriced merchants — resolved once from config (None unless configured).
        services.Configure<MerchantDefaultFeeOptions>(configuration.GetSection(MerchantDefaultFeeOptions.SectionName));

        // Whether staff actions in this module screen an address before accepting it. Kept separate from
        // Compliance's own settings and from Withdrawal:Screening — each consumer decides what a verdict
        // means for its own flow (§ MerchantScreeningOptions).
        services.Configure<MerchantScreeningOptions>(configuration.GetSection(MerchantScreeningOptions.SectionName));
        services.AddSingleton<MerchantDefaultFee>();

        // TryAdd: a host (or a test) may supply a fake clock; the module must not stomp on it.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IApiSecretHasher, HmacApiSecretHasher>();
        services.AddSingleton<ISecretCipher, AesGcmSecretCipher>();
        services.AddSingleton<IApiCredentialGenerator, ApiCredentialGenerator>();
        services.AddScoped<IMerchantRepository, MerchantRepository>();
        services.AddScoped<IMerchantDirectory, MerchantDirectory>();
        services.AddScoped<IMerchantFeeSchedule, MerchantFeeSchedule>();
        services.AddScoped<IMerchantSettlementDirectory, MerchantSettlementDirectory>();
        services.AddScoped<IMerchantWithdrawalCap, MerchantWithdrawalCapReader>();
        services.AddScoped<IMerchantWithdrawalLimits, MerchantWithdrawalLimitsReader>();
        services.AddScoped<IMerchantDepositLimits, MerchantDepositLimitsReader>();
        services.AddScoped<IMerchantApprovalThreshold, MerchantApprovalThresholdReader>();
        // Built by hand rather than by convention because the screening provider is resolved SOFTLY
        // (GetService, not GetRequiredService). A host that never whitelists a settlement wallet — the
        // merchant portal — must not be forced to compose Compliance just to satisfy this graph (§15.10).
        // With settlement screening enabled but no provider composed, the registrar fails loudly at the call
        // rather than quietly skipping the check.
        services.AddScoped<IMerchantRegistrar>(sp => new MerchantRegistrar(
            sp.GetRequiredService<IMerchantRepository>(),
            sp.GetRequiredService<IApiCredentialGenerator>(),
            sp.GetRequiredService<IApiSecretHasher>(),
            sp.GetRequiredService<ISecretCipher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IAddressScreeningService>(),
            sp.GetRequiredService<IOptions<MerchantScreeningOptions>>()));
        services.AddScoped<IMerchantAssetPolicyService, MerchantAssetPolicyService>();

        // Same soft resolution as the registrar above, and for the same reason: a host that composes
        // Merchant but not Compliance must still boot. The service reports loudly if re-screening is
        // switched on where no provider is composed, rather than quietly checking nothing.
        services.AddScoped(sp => new SettlementWalletScreeningService(
            sp.GetRequiredService<IMerchantSettlementDirectory>(),
            sp.GetService<IAddressScreeningService>(),
            sp.GetRequiredService<IOptions<MerchantScreeningOptions>>(),
            sp.GetRequiredService<ILogger<SettlementWalletScreeningService>>()));
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
