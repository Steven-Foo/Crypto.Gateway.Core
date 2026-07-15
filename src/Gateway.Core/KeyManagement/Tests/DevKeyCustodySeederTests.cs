using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CryptoPaymentEngine.SharedKernel;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Proves the config-driven dev custody path: <c>AddDevelopmentKeyCustody</c> registers the in-memory
/// secret provider from <c>KeyManagement:DevSecrets</c>, the hosted seeder materialises the
/// <c>KeyManagement:DevWallets</c> row, and a real allocation then derives the address that the configured
/// xpub determines — so substituting a different xpub (e.g. a production branch xpub) yields that xpub's
/// addresses locally, with no code change. Runs against a real SQL Server: the atomic index allocation is
/// a database guarantee.
/// </summary>
public sealed class DevKeyCustodySeederTests : IAsyncLifetime
{
    private const string DbName = "CpeDevKeyCustodySeederTests";
    private const string XpubReference = "dev/tron/deposit/xpub";
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>The published vector: index 0 of m/44'/195'/0'/0 for the test mnemonic.</summary>
    private const string ExpectedFirstTronAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>The account/branch xpub a developer would paste into config. Public material.</summary>
    private static string TronBranchXpub =>
        ExtKey.CreateFromSeed(new Mnemonic(TestMnemonic, Wordlist.English).DeriveSeed())
            .Derive(new KeyPath("44'/195'/0'/0")).Neuter().ToString(Network.Main);

    private ServiceProvider _provider = null!;

    private static IConfiguration BuildConfig(string xpub) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyManagement:DevWallets:0:Name"] = "TRON deposit pool (dev)",
            ["KeyManagement:DevWallets:0:Chain"] = "Tron",
            ["KeyManagement:DevWallets:0:Purpose"] = "Deposit",
            ["KeyManagement:DevWallets:0:DerivationPath"] = "m/44'/195'/0'/0",
            ["KeyManagement:DevWallets:0:SecretReference"] = "dev/tron/deposit/seed",
            ["KeyManagement:DevWallets:0:PublicKeyReference"] = XpubReference,
            [$"KeyManagement:DevSecrets:{XpubReference}"] = xpub,
        }).Build();

    private ServiceProvider BuildHost(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyManagementModule(ConnectionString);
        services.AddBlockchainAddressEncoding();
        services.AddDevelopmentKeyCustody(configuration);
        return services.BuildServiceProvider();
    }

    private async Task RunSeederAsync(ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(Ct);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();

        await using var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
        await context.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Seeder_creates_the_wallet_and_the_configured_xpub_derives_its_address()
    {
        _provider = BuildHost(BuildConfig(TronBranchXpub));
        await RunSeederAsync(_provider);

        // The wallet row now exists, InMemoryDevelopment-backed.
        await using (var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options))
        {
            var wallet = await context.HdWallets.SingleAsync(Ct);
            wallet.Chain.ShouldBe(Chain.Tron);
            wallet.Purpose.ShouldBe(HdWalletPurpose.Deposit);
            wallet.SecretProvider.ShouldBe(SecretProviderKind.InMemoryDevelopment);
        }

        // A real allocation derives the address the configured xpub determines.
        await using var scope = _provider.CreateAsyncScope();
        var derivation = scope.ServiceProvider.GetRequiredService<IWalletDerivation>();
        var result = await derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(ExpectedFirstTronAddress);
    }

    [Fact]
    public async Task Seeding_is_idempotent_across_restarts()
    {
        _provider = BuildHost(BuildConfig(TronBranchXpub));

        await RunSeederAsync(_provider);
        await RunSeederAsync(_provider); // second "startup" must not create a duplicate or throw

        await using var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
        (await context.HdWallets.CountAsync(Ct)).ShouldBe(1);
    }
}
