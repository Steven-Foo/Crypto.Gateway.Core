using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// Proves the dev merchant seeder produces a credential the real verifier accepts: seed → sign a request the
/// way a client would (<c>HMAC-SHA256(hexDecode(SigningSecret), "{ts}\n{body}")</c>) → the module's
/// <see cref="IMerchantRequestVerifier"/> resolves the merchant. This is the auth half of the dev deposit
/// round-trip, proven end to end against a real SQL Server.
/// </summary>
public sealed class DevMerchantSeederTests : IAsyncLifetime
{
    private const string DbName = "CpeDevMerchantSeederTests";
    private const string ApiKey = "cpe_dev_test";
    private const string SigningSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string MerchantCode = "DEVTESTMERCHANT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Merchant:ApiCredentials:CurrentHashVersion"] = "1",
            ["Merchant:ApiCredentials:Peppers:1"] = "test-pepper",
            ["Merchant:SigningSecrets:CurrentKeyVersion"] = "1",
            ["Merchant:SigningSecrets:Keys:1"] = Convert.ToBase64String(new byte[32]),
            ["Merchant:DevSeed:Enabled"] = "true",
            ["Merchant:DevSeed:MerchantCode"] = MerchantCode,
            ["Merchant:DevSeed:Name"] = "Dev Test Merchant",
            ["Merchant:DevSeed:ApiKey"] = ApiKey,
            ["Merchant:DevSeed:ApiSecret"] = "dev-bearer-secret",
            ["Merchant:DevSeed:SigningSecret"] = SigningSecret,
        }).Build();

    private static MerchantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMerchantModule(BuildConfig(), ConnectionString);
        services.AddDevelopmentMerchantSeed(BuildConfig());
        return services.BuildServiceProvider();
    }

    private async Task RunSeederAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StartAsync(Ct);
    }

    /// <summary>Signs exactly as a merchant client would, so the verifier is exercised on realistic input.</summary>
    private static string Sign(string timestamp, string body)
    {
        var key = Convert.FromHexString(SigningSecret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{timestamp}\n{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();

        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Seeded_credential_verifies_a_correctly_signed_request()
    {
        _provider = BuildHost();
        await RunSeederAsync();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string body = """{"paymentMethod":"USDT-TRON","amount":"10.5"}""";
        var signature = Sign(timestamp, body);

        await using var scope = _provider.CreateAsyncScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IMerchantRequestVerifier>();
        var repository = scope.ServiceProvider.GetRequiredService<IMerchantRepository>();

        var expectedMerchant = await repository.GetByCodeAsync(MerchantCode, Ct);
        expectedMerchant.ShouldNotBeNull();

        var result = await verifier.VerifyAsync(ApiKey, timestamp, body, signature, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedMerchant.Id);
    }

    [Fact]
    public async Task A_tampered_signature_is_rejected()
    {
        _provider = BuildHost();
        await RunSeederAsync();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string body = """{"paymentMethod":"USDT-TRON","amount":"10.5"}""";
        var tampered = Sign(timestamp, body + "x"); // signature over a different body

        await using var scope = _provider.CreateAsyncScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IMerchantRequestVerifier>();

        var result = await verifier.VerifyAsync(ApiKey, timestamp, body, tampered, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
    }

    [Fact]
    public async Task Seeding_is_idempotent_across_restarts()
    {
        _provider = BuildHost();

        await RunSeederAsync();
        await RunSeederAsync(); // a second "startup" must not create a duplicate or throw

        await using var context = NewContext();
        (await context.Merchants.CountAsync(m => m.MerchantCode == MerchantCode, Ct)).ShouldBe(1);
    }
}
