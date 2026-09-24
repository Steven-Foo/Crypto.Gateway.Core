using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure;

/// <summary>The Identity module's composition: staff login/logout/session validation for Ops hosts.</summary>
public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<IdentityDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", IdentityDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<StaffAuthOptions>(configuration.GetSection(StaffAuthOptions.SectionName));

        services.AddScoped<IStaffUserRepository, StaffUserRepository>();
        services.AddScoped<IStaffSessionRepository, StaffSessionRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IStaffPasswordHasher, StaffPasswordHasher>();
        services.AddScoped<IStaffPasswordGenerator, StaffPasswordGenerator>();
        services.AddScoped<IBearerTokenGenerator, BearerTokenGenerator>();

        // One class serves both application interfaces (§ StaffAuthService doc comment).
        services.AddScoped<StaffAuthService>();
        services.AddScoped<IStaffAuthService>(sp => sp.GetRequiredService<StaffAuthService>());
        services.AddScoped<IStaffSessionValidator>(sp => sp.GetRequiredService<StaffAuthService>());

        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IStaffAccountService, StaffAccountService>();

        AddTwoFactor(services, configuration);

        return services;
    }

    /// <summary>
    /// The second factor: enrollment, verification, and the platform-wide policy saying which actions demand
    /// a code.
    ///
    /// <para>Registered unconditionally, with no enable switch. Gating registration on configuration would
    /// mean an account that has enrolled could find its factor silently not required after a settings
    /// change — a security control that can be turned off by omission is one nobody can rely on. What IS
    /// configurable is which actions are guarded, and that lives in the policy.</para>
    /// </summary>
    private static void AddTwoFactor(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TwoFactorOptions>(configuration.GetSection(TwoFactorOptions.SectionName));
        services.Configure<TwoFactorSecretOptions>(configuration.GetSection(TwoFactorSecretOptions.SectionName));

        services.AddScoped<IStaffTwoFactorRepository, StaffTwoFactorRepository>();
        services.AddScoped<ITwoFactorPolicyRepository, TwoFactorPolicyRepository>();

        // Singleton: it holds decoded key bytes and validates them once, at composition, so a host with a
        // missing or malformed key refuses to start rather than failing mid-enrollment.
        services.AddSingleton<ITwoFactorSecretCipher, AesGcmTwoFactorSecretCipher>();

        services.AddScoped<ITwoFactorService, TwoFactorService>();

        // Singleton cache behind a scoped provider — a per-scope cache would expire every request and cache
        // nothing (the same pairing the screening policy uses).
        services.AddSingleton<TwoFactorPolicyCache>();
        services.AddScoped<TwoFactorPolicyProvider>();
        services.AddScoped<ITwoFactorPolicyService, TwoFactorPolicyService>();
    }

    /// <summary>DEVELOPMENT ONLY — see <see cref="DevStaffSeeder"/>.</summary>
    public static IServiceCollection AddDevelopmentStaffSeed(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevStaffSeedOptions>(configuration.GetSection(DevStaffSeedOptions.SectionName));
        services.AddHostedService<DevStaffSeeder>();
        return services;
    }
}
