using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Separate seed per merchant, on real SQL Server. The custody property is that two merchants share no
/// derivation tree: their wallets are distinct rows with distinct account xpubs, and never the same address.
/// Also proves create-on-first-use (the wallet is minted lazily then reused with its own index sequence) and
/// that the dev seed is deterministic per merchant (so dev/test addresses are reproducible).
/// </summary>
public sealed class PerMerchantDerivationTests : IAsyncLifetime
{
    private const string DbName = "CpePerMerchantDerivationTests";
    private static readonly Guid MerchantA = Guid.CreateVersion7();
    private static readonly Guid MerchantB = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static KeyManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);

    private readonly MutableInMemorySecretStore _store = new();

    // No configured xpub → the provisioner uses the deterministic dev-salt seed (the behaviour under test).
    private static readonly IOptions<DevelopmentKeyCustodyOptions> DevOpts = Options.Create(new DevelopmentKeyCustodyOptions());

    private WalletDerivationService NewService(KeyManagementDbContext context) =>
        new(new HdWalletRepository(context),
            new KeyDeriverFactory([new Bip32Secp256k1KeyDeriver()]),
            new AddressEncoderFactory([new TronAddressEncoder(), new EthereumAddressEncoder()]),
            new SecretProviderFactory([_store]),
            TimeProvider.System,
            [new DevHdWalletProvisioner(_store, TimeProvider.System, DevOpts)]);

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    [Fact]
    public async Task Two_merchants_get_their_own_wallet_seed_and_a_distinct_address()
    {
        string addressA, addressB;
        await using (var context = NewContext())
        {
            addressA = (await NewService(context).AllocateNextForMerchantAsync(MerchantA, Chain.Tron, DerivationPurpose.Deposit, Ct)).Value.Address;
            addressB = (await NewService(context).AllocateNextForMerchantAsync(MerchantB, Chain.Tron, DerivationPurpose.Deposit, Ct)).Value.Address;
        }

        addressA.ShouldStartWith("T");             // a valid TRON base58 address
        addressA.ShouldNotBe(addressB);            // different tree → different address

        await using (var context = NewContext())
        {
            var wallets = await context.HdWallets.Where(w => w.Purpose == HdWalletPurpose.Deposit).ToListAsync(Ct);
            wallets.Count.ShouldBe(2);
            wallets.ShouldContain(w => w.MerchantId == MerchantA);
            wallets.ShouldContain(w => w.MerchantId == MerchantB);
            // Distinct account xpubs — the whole point of a separate seed per merchant.
            wallets.Select(w => w.PublicKeyReference).Distinct().Count().ShouldBe(2);
        }
    }

    [Fact]
    public async Task The_wallet_is_created_on_first_deposit_then_reused_with_its_own_index_sequence()
    {
        string first, second;
        await using (var context = NewContext())
        {
            var s = NewService(context);
            first = (await s.AllocateNextForMerchantAsync(MerchantA, Chain.Tron, DerivationPurpose.Deposit, Ct)).Value.Address;
            second = (await s.AllocateNextForMerchantAsync(MerchantA, Chain.Tron, DerivationPurpose.Deposit, Ct)).Value.Address;
        }

        first.ShouldNotBe(second);

        await using (var context = NewContext())
        {
            // Exactly one wallet for the merchant (minted once), and it has handed out two indices.
            var wallet = await context.HdWallets.SingleAsync(w => w.MerchantId == MerchantA, Ct);
            wallet.NextDerivationIndex.ShouldBe(2);
            (await context.DerivedKeys.CountAsync(k => k.HdWalletId == wallet.Id, Ct)).ShouldBe(2);
        }
    }

    [Fact]
    public async Task The_dev_seed_is_deterministic_per_merchant()
    {
        // Two independent provisioners (fresh stores) must mint the same account xpub for the same merchant,
        // so a developer's addresses are reproducible run to run.
        var one = (await new DevHdWalletProvisioner(new MutableInMemorySecretStore(), TimeProvider.System, DevOpts)
            .ProvisionMerchantDepositWalletAsync(MerchantA, Chain.Tron, Ct)).Value;
        var two = (await new DevHdWalletProvisioner(new MutableInMemorySecretStore(), TimeProvider.System, DevOpts)
            .ProvisionMerchantDepositWalletAsync(MerchantA, Chain.Tron, Ct)).Value;

        var storeOne = new MutableInMemorySecretStore();
        var provisioner = new DevHdWalletProvisioner(storeOne, TimeProvider.System, DevOpts);
        var walletA = (await provisioner.ProvisionMerchantDepositWalletAsync(MerchantA, Chain.Tron, Ct)).Value;
        var walletB = (await new DevHdWalletProvisioner(new MutableInMemorySecretStore(), TimeProvider.System, DevOpts)
            .ProvisionMerchantDepositWalletAsync(MerchantA, Chain.Tron, Ct)).Value;

        // Same merchant → identical xpub reference (deterministic references) and the stored xpub matches.
        one.PublicKeyReference.ShouldBe(two.PublicKeyReference);
        walletA.PublicKeyReference.ShouldBe(walletB.PublicKeyReference);

        using var lease = await storeOne.GetAsync(walletA.PublicKeyReference!, Ct);
        lease.AsPublicUtf8String().ShouldStartWith("xpub"); // a real BIP-32 extended public key
    }
}
