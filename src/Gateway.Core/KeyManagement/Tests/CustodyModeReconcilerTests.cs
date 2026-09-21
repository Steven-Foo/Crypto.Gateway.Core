using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// The testnet custody-mode switch (in-memory store ↔ AWS KMS). Runs against a real SQL Server, because what makes the
/// switch correct is database behaviour: the filtered one-active-wallet index, the transaction, and the derivation
/// index a restored wallet must resume from.
/// </summary>
public sealed class CustodyModeReconcilerTests : IAsyncLifetime
{
    private const string DbName = "CpeCustodyModeReconcilerTests";
    private const string Path = "m/44'/195'/0'/0";
    private const SecretProviderKind InMemory = SecretProviderKind.InMemoryDevelopment;
    private const SecretProviderKind Kms = SecretProviderKind.AwsKmsEnvelope;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private readonly StepClock _clock = new(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var context = NewContext())
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddKeyManagementModule(ConnectionString);
        services.AddScoped<CustodyModeReconciler>();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Switching_to_KMS_archives_the_in_memory_wallets_and_frees_the_slot_for_a_KMS_wallet()
    {
        var merchant = Guid.CreateVersion7();
        var deposit = await AddMerchantWallet(merchant, InMemory);
        var pool = await AddPoolWallet(InMemory);

        (await SwitchTo(Kms)).ShouldBe(new CustodySwitchOutcome(Archived: 2, Reactivated: 0));

        (await Load(deposit.Id)).Status.ShouldBe(HdWalletStatus.Archived);
        (await Load(pool.Id)).Status.ShouldBe(HdWalletStatus.Archived);

        // The provisioner can now mint the merchant's KMS wallet: the one-active-wallet index no longer blocks it.
        await AddMerchantWallet(merchant, Kms);
    }

    [Fact]
    public async Task Switching_back_restores_the_archived_wallet_with_its_derivation_index_intact()
    {
        var merchant = Guid.CreateVersion7();
        var inMemory = await AddMerchantWallet(merchant, InMemory);
        await Allocate(inMemory.Id, times: 3);

        _clock.Advance(TimeSpan.FromHours(1));
        await SwitchTo(Kms);
        var kms = await AddMerchantWallet(merchant, Kms);
        await Allocate(kms.Id, times: 1);

        _clock.Advance(TimeSpan.FromHours(1));
        (await SwitchTo(InMemory)).ShouldBe(new CustodySwitchOutcome(Archived: 1, Reactivated: 1));

        var restored = await Load(inMemory.Id);
        restored.Status.ShouldBe(HdWalletStatus.Active);
        restored.NextDerivationIndex.ShouldBe(3); // resumes after the three addresses it issued, never reusing one
        (await Load(kms.Id)).Status.ShouldBe(HdWalletStatus.Archived);

        // And the other way again: the KMS wallet comes back with its own index.
        _clock.Advance(TimeSpan.FromHours(1));
        (await SwitchTo(Kms)).ShouldBe(new CustodySwitchOutcome(Archived: 1, Reactivated: 1));
        var kmsRestored = await Load(kms.Id);
        kmsRestored.Status.ShouldBe(HdWalletStatus.Active);
        kmsRestored.NextDerivationIndex.ShouldBe(1);
        (await Load(inMemory.Id)).Status.ShouldBe(HdWalletStatus.Archived);
    }

    [Fact]
    public async Task Running_again_in_the_same_mode_changes_nothing()
    {
        await AddMerchantWallet(Guid.CreateVersion7(), InMemory);

        (await SwitchTo(InMemory)).ShouldBe(new CustodySwitchOutcome(0, 0));
        (await SwitchTo(Kms)).ShouldBe(new CustodySwitchOutcome(1, 0));
        (await SwitchTo(Kms)).ShouldBe(new CustodySwitchOutcome(0, 0));
    }

    [Fact]
    public async Task A_disabled_wallet_is_never_restored()
    {
        var wallet = await AddMerchantWallet(Guid.CreateVersion7(), InMemory);
        await using (var scope = _services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();
            var tracked = await repository.FindByIdAsync(wallet.Id, Ct);
            tracked!.Disable(_clock.GetUtcNow());
            await repository.SaveChangesAsync(Ct);
        }

        (await SwitchTo(InMemory)).ShouldBe(new CustodySwitchOutcome(0, 0));
        (await Load(wallet.Id)).Status.ShouldBe(HdWalletStatus.Disabled);
    }

    [Fact]
    public async Task When_two_archived_wallets_qualify_the_most_recently_archived_one_is_restored()
    {
        var merchant = Guid.CreateVersion7();
        var older = await AddMerchantWallet(merchant, InMemory);

        _clock.Advance(TimeSpan.FromHours(1));
        await SwitchTo(Kms);                                        // archives `older`

        var newer = await AddMerchantWallet(merchant, InMemory);   // the slot is free, so a second one exists
        _clock.Advance(TimeSpan.FromHours(1));
        await SwitchTo(Kms);                                        // archives `newer`, later than `older`

        _clock.Advance(TimeSpan.FromHours(1));
        (await SwitchTo(InMemory)).ShouldBe(new CustodySwitchOutcome(Archived: 0, Reactivated: 1));

        (await Load(newer.Id)).Status.ShouldBe(HdWalletStatus.Active);
        (await Load(older.Id)).Status.ShouldBe(HdWalletStatus.Archived);
    }

    [Fact]
    public void Only_an_archived_wallet_can_be_reactivated()
    {
        var now = _clock.GetUtcNow();

        var active = NewMerchantWallet(Guid.CreateVersion7(), InMemory);
        active.Reactivate(now).Error!.Code.ShouldBe(KeyManagementErrors.NotArchived.Code);

        var disabled = NewMerchantWallet(Guid.CreateVersion7(), InMemory);
        disabled.Disable(now);
        disabled.Reactivate(now).IsFailure.ShouldBeTrue();
        disabled.Status.ShouldBe(HdWalletStatus.Disabled);

        var archived = NewMerchantWallet(Guid.CreateVersion7(), InMemory);
        archived.Archive(now);
        archived.Reactivate(now).IsSuccess.ShouldBeTrue();
        archived.Status.ShouldBe(HdWalletStatus.Active);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static KeyManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);

    private async Task<CustodySwitchOutcome> SwitchTo(SecretProviderKind kind)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CustodyModeReconciler>().ReconcileAsync(kind, Ct);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    private HdWallet NewMerchantWallet(Guid merchantId, SecretProviderKind kind)
    {
        var created = HdWallet.CreateMerchantDeposit(
            merchantId, "merchant deposit", Chain.Tron, kind, $"ref/{Guid.NewGuid():N}", $"xpub/{Guid.NewGuid():N}", Path,
            timeProvider: _clock);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        return created.Value;
    }

    private async Task<HdWallet> AddMerchantWallet(Guid merchantId, SecretProviderKind kind) =>
        await Insert(NewMerchantWallet(merchantId, kind));

    private async Task<HdWallet> AddPoolWallet(SecretProviderKind kind)
    {
        var created = HdWallet.Create(
            "withdrawal pool", Chain.Tron, HdWalletPurpose.Withdrawal, kind, $"ref/{Guid.NewGuid():N}", $"xpub/{Guid.NewGuid():N}",
            Path, timeProvider: _clock);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        return await Insert(created.Value);
    }

    private async Task<HdWallet> Insert(HdWallet wallet)
    {
        await using var scope = _services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();
        (await repository.TryAddActiveAsync(wallet, Ct)).ShouldBe(HdWalletAddOutcome.Added);
        return wallet;
    }

    private async Task Allocate(Guid walletId, int times)
    {
        await using var scope = _services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();
        for (var i = 0; i < times; i++)
            (await repository.AllocateNextIndexAsync(walletId, Ct)).IsSuccess.ShouldBeTrue();
    }

    private static async Task<HdWallet> Load(Guid walletId)
    {
        await using var context = NewContext();
        return await context.HdWallets.AsNoTracking().SingleAsync(w => w.Id == walletId, Ct);
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>
/// How a testnet host composes its secret store. Inspects registrations rather than resolving them, so no AWS
/// credentials or database are needed: what matters is that exactly one store is wired, and in the right order.
/// </summary>
public sealed class TestnetKeyCustodyCompositionTests
{
    private const string DepositArn = "arn:aws:kms:ap-southeast-1:111:key/testnet-deposit";
    private const string WithdrawalArn = "arn:aws:kms:ap-southeast-1:111:key/testnet-withdrawal";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static IConfiguration KmsOn() => Config(
        ("KeyManagement:Kms:Enabled", "true"),
        ("KeyManagement:Kms:Region", "ap-southeast-1"),
        ("KeyManagement:Kms:KeyArns:Deposit", DepositArn),
        ("KeyManagement:Kms:KeyArns:Withdrawal", WithdrawalArn));

    private static bool Has<TService, TImplementation>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(TService) && d.ImplementationType == typeof(TImplementation));

    [Fact]
    public void Without_the_KMS_switch_the_tier_keeps_the_in_memory_store()
    {
        var services = new ServiceCollection();

        services.AddTestnetKeyCustody(Config()).ShouldBeFalse();

        Has<ISecretProvider, DevHdWalletSigningSecretProvider>(services).ShouldBeTrue();
        Has<ISecretProvider, KmsEnvelopeSecretProvider>(services).ShouldBeFalse();
        Has<IHdWalletProvisioner, KmsHdWalletProvisioner>(services).ShouldBeFalse();
    }

    [Fact]
    public void With_the_KMS_switch_the_tier_takes_KMS_custody_and_none_of_the_in_memory_store()
    {
        var services = new ServiceCollection();

        services.AddTestnetKeyCustody(KmsOn()).ShouldBeTrue();

        Has<ISecretProvider, KmsEnvelopeSecretProvider>(services).ShouldBeTrue();
        Has<IHdWalletProvisioner, KmsHdWalletProvisioner>(services).ShouldBeTrue();
        services.ShouldNotContain(d => d.ServiceType == typeof(ISecretProvider) && d.ImplementationType != typeof(KmsEnvelopeSecretProvider));
        services.ShouldNotContain(d => d.ServiceType == typeof(DevHdWalletProvisioner));
        services.ShouldNotContain(d => d.ImplementationType == typeof(DevHdWalletReseeder));
        services.ShouldNotContain(d => d.ImplementationType == typeof(DevHdWalletSeeder));
    }

    [Theory]
    [InlineData("KeyManagement:Kms:Region")]
    [InlineData("KeyManagement:Kms:KeyArns:Deposit")]
    [InlineData("KeyManagement:Kms:KeyArns:Withdrawal")]
    public void KMS_switched_on_with_a_missing_identifier_refuses_to_compose(string missing)
    {
        var values = new Dictionary<string, string?>
        {
            ["KeyManagement:Kms:Enabled"] = "true",
            ["KeyManagement:Kms:Region"] = "ap-southeast-1",
            ["KeyManagement:Kms:KeyArns:Deposit"] = DepositArn,
            ["KeyManagement:Kms:KeyArns:Withdrawal"] = WithdrawalArn,
        };
        values.Remove(missing);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddTestnetKeyCustody(config))
            .Message.ShouldContain("KeyManagement:Kms");
    }

    [Fact]
    public void The_switch_is_registered_only_when_asked_and_starts_before_the_in_memory_reseeder()
    {
        var withoutSwitch = new ServiceCollection();
        withoutSwitch.AddTestnetKeyCustody(Config());
        withoutSwitch.ShouldNotContain(d => d.ImplementationType == typeof(CustodyModeReconciliationService));

        var withSwitch = new ServiceCollection();
        withSwitch.AddTestnetKeyCustody(Config(), reconcileWallets: true);
        var hosted = withSwitch.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

        hosted.IndexOf(typeof(CustodyModeReconciliationService)).ShouldBe(0);
        hosted.IndexOf(typeof(DevHdWalletReseeder)).ShouldBeGreaterThan(0);
        withSwitch.ShouldContain(d => d.ServiceType == typeof(CustodyModeOptions));
    }
}
