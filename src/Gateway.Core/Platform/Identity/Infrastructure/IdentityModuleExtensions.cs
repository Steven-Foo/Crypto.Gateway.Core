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
        services.AddScoped<IStaffPasswordHasher, StaffPasswordHasher>();
        services.AddScoped<IBearerTokenGenerator, BearerTokenGenerator>();

        // One class serves both application interfaces (§ StaffAuthService doc comment).
        services.AddScoped<StaffAuthService>();
        services.AddScoped<IStaffAuthService>(sp => sp.GetRequiredService<StaffAuthService>());
        services.AddScoped<IStaffSessionValidator>(sp => sp.GetRequiredService<StaffAuthService>());

        return services;
    }

    /// <summary>DEVELOPMENT ONLY — see <see cref="DevStaffSeeder"/>.</summary>
    public static IServiceCollection AddDevelopmentStaffSeed(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevStaffSeedOptions>(configuration.GetSection(DevStaffSeedOptions.SectionName));
        services.AddHostedService<DevStaffSeeder>();
        return services;
    }
}
