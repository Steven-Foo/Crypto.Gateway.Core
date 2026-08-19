using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure;

/// <summary>The Audit module's composition: staff-action logging + search for Ops hosts.</summary>
public static class AuditModuleExtensions
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AuditDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", AuditDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IAuditEntryRepository, AuditEntryRepository>();

        // One class serves both application interfaces (§ AuditService doc comment).
        services.AddScoped<AuditService>();
        services.AddScoped<IAuditLogger>(sp => sp.GetRequiredService<AuditService>());
        services.AddScoped<IAuditQuery>(sp => sp.GetRequiredService<AuditService>());

        return services;
    }
}
