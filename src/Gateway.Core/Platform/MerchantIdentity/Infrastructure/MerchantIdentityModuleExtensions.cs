using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure;

/// <summary>The MerchantIdentity module's composition: merchant-portal login/logout/session validation, each
/// session bound to one tenant (<c>MerchantId</c>).</summary>
public static class MerchantIdentityModuleExtensions
{
    public static IServiceCollection AddMerchantIdentityModule(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<MerchantIdentityDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", MerchantIdentityDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<MerchantIdentityOptions>(configuration.GetSection(MerchantIdentityOptions.SectionName));

        services.AddScoped<IMerchantUserRepository, MerchantUserRepository>();
        services.AddScoped<IMerchantRoleRepository, MerchantRoleRepository>();
        services.AddScoped<IMerchantUserSessionRepository, MerchantUserSessionRepository>();
        services.AddScoped<IMerchantPasswordHasher, MerchantPasswordHasher>();
        services.AddScoped<IMerchantSessionTokenGenerator, MerchantSessionTokenGenerator>();
        services.AddScoped<IMerchantPasswordGenerator, MerchantPasswordGenerator>();

        // One class serves both application interfaces (§ MerchantAuthService).
        services.AddScoped<MerchantAuthService>();
        services.AddScoped<IMerchantAuthService>(sp => sp.GetRequiredService<MerchantAuthService>());
        services.AddScoped<IMerchantSessionValidator>(sp => sp.GetRequiredService<MerchantAuthService>());

        services.AddScoped<IMerchantRoleService, MerchantRoleService>();
        services.AddScoped<IMerchantAccountService, MerchantAccountService>();

        // Two-factor: registered unconditionally, with no enable switch. Gating registration on
        // configuration would let an enrolled account silently stop being asked for a code after a settings
        // change, and a control that can be turned off by omission is one nobody can rely on.
        services.Configure<MerchantTwoFactorSecretOptions>(
            configuration.GetSection(MerchantTwoFactorSecretOptions.SectionName));
        services.AddScoped<IMerchantTwoFactorRepository, MerchantTwoFactorRepository>();

        // Singleton: it decodes and validates its keys once, at composition, so a host with a missing or
        // malformed key refuses to start rather than failing mid-enrollment.
        services.AddSingleton<IMerchantTwoFactorSecretCipher, AesGcmMerchantTwoFactorSecretCipher>();
        services.AddScoped<IMerchantTwoFactorService, MerchantTwoFactorService>();

        return services;
    }

    /// <summary>DEVELOPMENT ONLY — see <see cref="DevMerchantPortalSeeder"/>.</summary>
    public static IServiceCollection AddDevelopmentMerchantPortalSeed(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevMerchantPortalSeedOptions>(configuration.GetSection(DevMerchantPortalSeedOptions.SectionName));
        services.AddHostedService<DevMerchantPortalSeeder>();
        return services;
    }
}
