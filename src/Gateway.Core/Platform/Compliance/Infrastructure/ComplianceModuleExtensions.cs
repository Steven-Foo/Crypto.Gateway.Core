using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure;

/// <summary>
/// The Compliance module's composition: address screening plus its evidence trail.
///
/// <para>The vendor adapter is chosen the same way the chain adapter is (§8) — by DI, not by a runtime
/// branch inside the service. A host registers exactly one <see cref="IAddressRiskProvider"/>, so there is
/// never a code path where a fake and a real provider could be confused for one another.</para>
/// </summary>
public static class ComplianceModuleExtensions
{
    /// <summary>Registers the module with the in-memory provider. Dev and test only — it fabricates a clean
    /// score for any unstaged address, which must never be mistaken for a real verdict (§10).</summary>
    public static IServiceCollection AddComplianceModuleWithInMemoryProvider(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        AddCore(services, configuration, connectionString);

        // Registered concretely as well as through the port, so a dev seeder or a test can reach Stage(...)
        // without casting. The Energy in-memory resource reader was registered port-only and that made a
        // demo step silently no-op; this avoids repeating it.
        services.TryAddSingleton<InMemoryAddressRiskProvider>();
        services.TryAddSingleton<IAddressRiskProvider>(sp => sp.GetRequiredService<InMemoryAddressRiskProvider>());

        return services;
    }

    /// <summary>
    /// Registers the module against the real MistTrack API. The base URL decides sandbox versus live, so
    /// the same code path is exercised in both — a sandbox-only adapter would prove nothing about the one
    /// that runs in production.
    /// </summary>
    public static IServiceCollection AddComplianceModuleWithMistTrack(
        this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        AddCore(services, configuration, connectionString);

        services.Configure<MistTrackOptions>(configuration.GetSection(MistTrackOptions.SectionName));

        // SINGLETON, and that is the whole point. The provider below is a typed HttpClient and therefore
        // TRANSIENT, so pacing held inside it would reset on every scope — correct within one worker pass
        // and no constraint at all between passes or between concurrent callers in the same process.
        services.AddSingleton<MistTrackRateLimiter>();

        var options = configuration.GetSection(MistTrackOptions.SectionName).Get<MistTrackOptions>()
                      ?? new MistTrackOptions();

        services.AddHttpClient<IAddressRiskProvider, MistTrackAddressRiskProvider>(client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
            })
            // Retries are deliberately conservative. A 429 here means the daily quota or the per-second
            // rate is already spent, so retrying it aggressively burns the very budget that is exhausted.
            // The adapter's own pacing is the primary defence; this handles transient transport faults.
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 2;
                resilience.Retry.UseJitter = true;
            });

        return services;
    }

    private static void AddCore(IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<ComplianceDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", ComplianceDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<ComplianceOptions>(configuration.GetSection(ComplianceOptions.SectionName));

        services.AddScoped<IAddressScreeningRepository, AddressScreeningRepository>();
        services.AddScoped<IScreeningPolicyRepository, ScreeningPolicyRepository>();

        // Singleton cache, scoped provider: the provider reads through a DbContext, but a per-scope cache
        // would expire every request and cache nothing. The short window is what carries a change made on
        // the ops host across to the money host workers without a restart.
        services.AddSingleton<ScreeningPolicyCache>();
        services.AddScoped<ScreeningPolicyProvider>();
        services.AddScoped<IScreeningPolicyService, ScreeningPolicyService>();
        services.AddScoped<IAddressScreeningService, AddressScreeningService>();

        // The staff read over the evidence trail. Registered in core rather than per-provider because it
        // reads stored rows and never contacts a vendor — it is equally valid behind the fake and the real
        // adapter.
        services.AddScoped<IAddressScreeningDirectory, AddressScreeningDirectory>();
    }
}
