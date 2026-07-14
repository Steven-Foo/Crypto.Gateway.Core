using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// The module's DI graph must actually resolve. In particular the peppers bind from configuration
/// into an <c>IReadOnlyDictionary&lt;int, string&gt;</c> — a silent binding failure here would only
/// surface as "no pepper configured" at first boot.
/// </summary>
public sealed class MerchantModuleCompositionTests
{
    private const string DummyConnection = "Server=(localdb)\\MSSQLLocalDB;Database=CpeUnused;Trusted_Connection=True";

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddMerchantModule(configuration, DummyConnection)
            .BuildServiceProvider();
    }

    [Fact]
    public void Module_services_resolve_from_configuration()
    {
        using var provider = BuildProvider(
            ("Merchant:ApiCredentials:CurrentHashVersion", "2"),
            ("Merchant:ApiCredentials:Peppers:1", "old-pepper"),
            ("Merchant:ApiCredentials:Peppers:2", "current-pepper"));

        using var scope = provider.CreateScope();

        var hasher = scope.ServiceProvider.GetRequiredService<IApiSecretHasher>();
        hasher.CurrentVersion.ShouldBe(2);

        // Both peppers bound: the old one must still verify existing credentials.
        var oldHash = new HmacApiSecretHasher(Microsoft.Extensions.Options.Options.Create(
            new ApiCredentialOptions { CurrentHashVersion = 1, Peppers = new Dictionary<int, string> { [1] = "old-pepper" } }))
            .Hash("s");

        hasher.Verify("s", oldHash, version: 1).ShouldBeTrue();

        scope.ServiceProvider.GetRequiredService<IApiCredentialGenerator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IMerchantRepository>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IMerchantDirectory>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IMerchantRegistrar>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IMerchantAuthenticator>().ShouldNotBeNull();
    }

    [Fact]
    public void Module_refuses_to_start_when_no_pepper_is_configured()
    {
        using var provider = BuildProvider(("Merchant:ApiCredentials:CurrentHashVersion", "1"));
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IApiSecretHasher>());
    }
}
