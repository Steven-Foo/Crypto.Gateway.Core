using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Proves <see cref="DevHdWalletReseeder"/> actually closes the restart gap: an already-existing dev HD
/// wallet's xpub, once repopulated, is (a) the exact deterministic value <see cref="DevHdWalletProvisioner"/>
/// would compute fresh, and (b) sufficient for <see cref="IWalletDerivation"/> to keep allocating from that
/// wallet — without minting a new one or touching any already-derived address. Runs against a real SQL
/// Server: the wallet rows and index allocation are database state, exactly as in production/testnet.
/// </summary>
public sealed class DevHdWalletReseederTests : IAsyncLifetime
{
    private const string DbName = "CpeDevHdWalletReseederTests";
    private static readonly Guid MerchantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    public async ValueTask InitializeAsync()
    {
        await using var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = new KeyManagementDbContext(
            new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
        await context.Database.EnsureDeletedAsync();
    }

    /// <summary>A DI container standing in for one process lifetime: its own store, its own provisioner
    /// instance over that store, wired to the shared database — so a fresh instance genuinely reproduces a
    /// restart (empty store, same DB rows) rather than merely clearing a dictionary in place.</summary>
    private static ServiceProvider BuildProcess()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyManagementModule(ConnectionString);
        services.AddBlockchainAddressEncoding();

        services.AddSingleton(new MutableInMemorySecretStore());
        services.AddSingleton<ISecretProvider>(sp => sp.GetRequiredService<MutableInMemorySecretStore>());
        services.AddSingleton(Options.Create(new DevelopmentKeyCustodyOptions()));
        services.AddSingleton<DevHdWalletProvisioner>();
        services.AddSingleton<IHdWalletProvisioner>(sp => sp.GetRequiredService<DevHdWalletProvisioner>());

        return services.BuildServiceProvider();
    }

    private static async Task<DevHdWalletReseeder> ReseederFor(ServiceProvider process) =>
        await Task.FromResult(new DevHdWalletReseeder(
            process.GetRequiredService<IServiceScopeFactory>(),
            process.GetRequiredService<MutableInMemorySecretStore>(),
            process.GetRequiredService<DevHdWalletProvisioner>(),
            NullLogger<DevHdWalletReseeder>.Instance));

    [Fact]
    public async Task An_existing_merchant_wallet_cannot_allocate_after_a_simulated_restart_without_reseeding()
    {
        await using var original = BuildProcess();
        await using (var scope = original.CreateAsyncScope())
        {
            var derivation = scope.ServiceProvider.GetRequiredService<IWalletDerivation>();
            var first = await derivation.AllocateNextForMerchantAsync(MerchantId, Chain.Tron, DerivationPurpose.Deposit, Ct);
            first.IsSuccess.ShouldBeTrue();
        }

        // A fresh process (empty store, same DB rows) reproduces the wiped-memory restart.
        await using var restarted = BuildProcess();
        await using var restartedScope = restarted.CreateAsyncScope();
        var derivationAfterRestart = restartedScope.ServiceProvider.GetRequiredService<IWalletDerivation>();

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await derivationAfterRestart.AllocateNextForMerchantAsync(MerchantId, Chain.Tron, DerivationPurpose.Deposit, Ct));
    }

    [Fact]
    public async Task Reseeding_restores_the_exact_deterministic_xpub_and_allocation_resumes()
    {
        await using var original = BuildProcess();
        string reference;
        await using (var scope = original.CreateAsyncScope())
        {
            var derivation = scope.ServiceProvider.GetRequiredService<IWalletDerivation>();
            var first = await derivation.AllocateNextForMerchantAsync(MerchantId, Chain.Tron, DerivationPurpose.Deposit, Ct);
            first.IsSuccess.ShouldBeTrue();

            await using var context = new KeyManagementDbContext(
                new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);
            var wallet = await context.HdWallets.SingleAsync(w => w.MerchantId == MerchantId, Ct);
            reference = wallet.PublicKeyReference!;
        }

        await using var restarted = BuildProcess();
        var reseeder = await ReseederFor(restarted);
        await reseeder.StartAsync(Ct);

        var restartedStore = restarted.GetRequiredService<MutableInMemorySecretStore>();
        var restartedProvisioner = restarted.GetRequiredService<DevHdWalletProvisioner>();

        using var lease = await restartedStore.GetAsync(reference, Ct);
        lease.AsPublicUtf8String().ShouldBe(restartedProvisioner.ResolveMerchantDepositXpub(MerchantId, Chain.Tron));

        // And allocation genuinely resumes — the second address, continuing the same tree.
        await using var restartedScope = restarted.CreateAsyncScope();
        var derivationAfterReseed = restartedScope.ServiceProvider.GetRequiredService<IWalletDerivation>();
        var second = await derivationAfterReseed.AllocateNextForMerchantAsync(MerchantId, Chain.Tron, DerivationPurpose.Deposit, Ct);

        second.IsSuccess.ShouldBeTrue();
        second.Value.DerivationIndex.ShouldBe(1);
    }

    [Fact]
    public async Task Reseeding_also_restores_the_platform_withdrawal_pool_wallet()
    {
        await using var original = BuildProcess();
        await using (var scope = original.CreateAsyncScope())
        {
            var derivation = scope.ServiceProvider.GetRequiredService<IWalletDerivation>();
            var first = await derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Ct);
            first.IsSuccess.ShouldBeTrue();
        }

        await using var restarted = BuildProcess();
        var reseeder = await ReseederFor(restarted);
        await reseeder.StartAsync(Ct);

        await using var restartedScope = restarted.CreateAsyncScope();
        var derivationAfterReseed = restartedScope.ServiceProvider.GetRequiredService<IWalletDerivation>();
        var second = await derivationAfterReseed.AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Ct);

        second.IsSuccess.ShouldBeTrue();
        second.Value.DerivationIndex.ShouldBe(1);
    }

    [Fact]
    public async Task Reseeding_with_no_existing_wallets_is_a_harmless_no_op()
    {
        await using var restarted = BuildProcess();
        var reseeder = await ReseederFor(restarted);

        await Should.NotThrowAsync(async () => await reseeder.StartAsync(Ct));
    }

    /// <summary>
    /// Regression: a platform wallet seeded directly from config (any purpose, e.g. a legacy platform
    /// <c>Deposit</c> pool — <see cref="DevHdWalletSeeder"/>'s job, not <see cref="DevHdWalletProvisioner"/>'s)
    /// already carries its correct xpub via <c>DevSecrets</c>. The reseeder must leave it alone — it is not
    /// one of the two shapes (merchant deposit / platform withdrawal pool) it is allowed to recompute, and
    /// recomputing it under either formula would silently overwrite a real, correct key with the wrong one.
    /// </summary>
    [Fact]
    public async Task Reseeding_never_overwrites_a_config_seeded_platform_wallet_of_a_different_purpose()
    {
        const string reference = "dev/tron/deposit/xpub";
        const string configuredXpub = "xpub6D4BDPcP2GT577Vvch3R8wDkScZWzQzMMUm3PWbmWvVJrZwQY4VUNgqFJPMM3No2SYyLnutx3vsvXbW5Fh4bByBnZ6ZvVRoY68eqvvHF6vh";

        await using var process = BuildProcess();
        await using (var scope = process.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IHdWalletRepository>();
            var wallet = HdWallet.Create(
                "platform-tron-deposit-legacy", Chain.Tron, HdWalletPurpose.Deposit,
                SecretProviderKind.InMemoryDevelopment,
                "dev/tron/deposit/seed", reference, "m/44'/195'/0'/0").Value;
            repository.Add(wallet);
            await repository.SaveChangesAsync(Ct);
        }

        var store = process.GetRequiredService<MutableInMemorySecretStore>();
        store.Put(reference, configuredXpub); // as AddDevelopmentKeyCustody would from KeyManagement:DevSecrets

        var reseeder = await ReseederFor(process);
        await reseeder.StartAsync(Ct);

        using var lease = await store.GetAsync(reference, Ct);
        lease.AsPublicUtf8String().ShouldBe(configuredXpub);
    }
}
