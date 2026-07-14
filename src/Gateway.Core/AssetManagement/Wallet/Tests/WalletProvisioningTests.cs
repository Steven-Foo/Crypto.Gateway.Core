using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NBitcoin;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;
using WalletEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain.Wallet;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Tests;

/// <summary>
/// Exercises the whole "merchant onboards → gets a dedicated deposit address" path across three
/// modules and their three schemas, against a real SQL Server. The address that comes out must be
/// the published BIP-44 vector, proving the modules compose without corrupting the derivation.
/// </summary>
public sealed class WalletProvisioningTests : IAsyncLifetime
{
    private const string DbName = "CpeWalletProvisioningTests";
    private const string PublicKeyReference = "arn:test:tron-xpub";
    private const string SecretReference = "arn:test:tron-seed";
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>The published vector: index 0 of m/44'/195'/0'/0 for the test mnemonic.</summary>
    private const string ExpectedFirstTronAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static string TronBranchXpub =>
        ExtKey.CreateFromSeed(new Mnemonic(TestMnemonic, Wordlist.English).DeriveSeed())
            .Derive(new KeyPath("44'/195'/0'/0")).Neuter().ToString(Network.Main);

    private static WalletDbContext WalletContext() =>
        new(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(ConnectionString).Options);

    private static KeyManagementDbContext KeyContext() =>
        new(new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);

    private static MerchantDbContext MerchantContext() =>
        new(new DbContextOptionsBuilder<MerchantDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private static WalletProvisioningService NewService(
        WalletDbContext walletContext,
        KeyManagementDbContext keyContext,
        MerchantDbContext merchantContext)
    {
        var derivation = new WalletDerivationService(
            new HdWalletRepository(keyContext),
            new KeyDeriverFactory([new Bip32Secp256k1KeyDeriver()]),
            new AddressEncoderFactory([new TronAddressEncoder(), new EthereumAddressEncoder()]),
            new SecretProviderFactory([InMemorySecretProvider.FromStrings(
                new Dictionary<string, string> { [PublicKeyReference] = TronBranchXpub })]),
            TimeProvider.System);

        return new WalletProvisioningService(
            new WalletRepository(walletContext),
            derivation,
            new MerchantDirectory(merchantContext),
            TimeProvider.System);
    }

    public async ValueTask InitializeAsync()
    {
        // One physical database, three module schemas. EnsureCreated is all-or-nothing per database,
        // so only the first context could use it; the others create just their own tables.
        await using (var wallet = WalletContext())
        {
            await wallet.Database.EnsureDeletedAsync();
            await wallet.Database.EnsureCreatedAsync();
        }
        await using (var key = KeyContext())
            await key.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
        await using (var merchant = MerchantContext())
            await merchant.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var wallet = WalletContext();
        await wallet.Database.EnsureDeletedAsync();
    }

    private static async Task<Guid> SeedActiveMerchantAsync(bool active = true)
    {
        await using var context = MerchantContext();
        var merchant = MerchantEntity.Create($"ACME-{Guid.NewGuid():N}"[..12], "Acme", null).Value;
        if (active) merchant.Activate(DateTimeOffset.UtcNow);
        context.Merchants.Add(merchant);
        await context.SaveChangesAsync(Ct);
        return merchant.Id;
    }

    private static async Task SeedTronDepositHdWalletAsync()
    {
        await using var context = KeyContext();
        context.HdWallets.Add(HdWallet.Create(
            "TRON deposit pool", Chain.Tron, HdWalletPurpose.Deposit,
            SecretProviderKind.InMemoryDevelopment, SecretReference, PublicKeyReference,
            "m/44'/195'/0'/0").Value);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Provisioning_yields_the_published_vector_address_and_persists_wallet_plus_assignment()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync();

        ProvisionedDepositAddress provisioned;
        await using (var wallet = WalletContext())
        await using (var key = KeyContext())
        await using (var merchant = MerchantContext())
        {
            var result = await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct);
            result.IsSuccess.ShouldBeTrue();
            provisioned = result.Value;
        }

        provisioned.Address.ShouldBe(ExpectedFirstTronAddress);

        await using (var context = WalletContext())
        {
            var wallet = await context.Wallets.Include(w => w.Assignments).SingleAsync(Ct);
            wallet.Chain.ShouldBe(Chain.Tron);
            wallet.Address.ShouldBe(ExpectedFirstTronAddress);
            wallet.WalletType.ShouldBe(WalletType.Deposit);
            wallet.MerchantId.ShouldBe(merchantId);
            wallet.Assignments.Count(a => a.IsActive).ShouldBe(1);
        }

        // The derived key really exists in custody, and the wallet references it.
        await using (var key = KeyContext())
        {
            var derivedKey = await key.DerivedKeys.SingleAsync(Ct);
            derivedKey.Address.ShouldBe(ExpectedFirstTronAddress);
            derivedKey.DerivationIndex.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Successive_provisioning_gives_each_call_a_distinct_address()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync();

        var addresses = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await using var wallet = WalletContext();
            await using var key = KeyContext();
            await using var merchant = MerchantContext();
            addresses.Add((await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct)).Value.Address);
        }

        addresses.Distinct().Count().ShouldBe(4);
        addresses[0].ShouldBe(ExpectedFirstTronAddress);
    }

    [Fact]
    public async Task Provisioning_is_refused_for_a_merchant_that_cannot_transact()
    {
        var merchantId = await SeedActiveMerchantAsync(active: false); // Pending
        await SeedTronDepositHdWalletAsync();

        await using var wallet = WalletContext();
        await using var key = KeyContext();
        await using var merchant = MerchantContext();

        var result = await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct);

        result.Error!.Code.ShouldBe(WalletErrors.MerchantCannotTransact.Code);

        // ...and nothing was derived or stored — a rejected merchant burns no index.
        await using var verifyKey = KeyContext();
        (await verifyKey.DerivedKeys.CountAsync(Ct)).ShouldBe(0);
        await using var verifyWallet = WalletContext();
        (await verifyWallet.Wallets.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Provisioning_is_refused_for_an_unknown_merchant()
    {
        await SeedTronDepositHdWalletAsync();

        await using var wallet = WalletContext();
        await using var key = KeyContext();
        await using var merchant = MerchantContext();

        var result = await NewService(wallet, key, merchant)
            .ProvisionDepositAddressAsync(Guid.CreateVersion7(), Chain.Tron, Ct);

        result.Error!.Code.ShouldBe(WalletErrors.MerchantNotFound.Code);
    }

    [Fact]
    public async Task Provisioning_fails_cleanly_when_no_deposit_pool_exists_for_the_chain()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync(); // TRON only

        await using var wallet = WalletContext();
        await using var key = KeyContext();
        await using var merchant = MerchantContext();

        var result = await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Ethereum, Ct);

        result.Error!.Code.ShouldBe(KeyManagementErrors.NotFound.Code);
    }

    // ── Database-level invariants ─────────────────────────────────────────────

    [Fact]
    public async Task Two_wallets_cannot_share_a_derived_key()
    {
        var derivedKeyId = Guid.CreateVersion7();

        await using var context = WalletContext();
        context.Wallets.Add(WalletEntity.CreatePlatform(derivedKeyId, Chain.Tron, "TAddrA", WalletType.Treasury).Value);
        await context.SaveChangesAsync(Ct);

        context.Wallets.Add(WalletEntity.CreatePlatform(derivedKeyId, Chain.Tron, "TAddrB", WalletType.Cold).Value);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        ex.InnerException.ShouldBeOfType<SqlException>().Message.ShouldContain("IX_Wallet_DerivedKeyId");
    }

    [Fact]
    public async Task Two_wallets_cannot_share_a_chain_and_address()
    {
        await using var context = WalletContext();
        context.Wallets.Add(WalletEntity.CreatePlatform(Guid.CreateVersion7(), Chain.Tron, "TSame", WalletType.Treasury).Value);
        await context.SaveChangesAsync(Ct);

        context.Wallets.Add(WalletEntity.CreatePlatform(Guid.CreateVersion7(), Chain.Tron, "TSame", WalletType.Cold).Value);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        ex.InnerException.ShouldBeOfType<SqlException>().Message.ShouldContain("IX_Wallet_Chain_Address");
    }

    /// <summary>
    /// The filtered unique index is the real guard against double-assigning a deposit address. Prove
    /// it by inserting a second Active assignment row for one wallet via raw SQL.
    /// </summary>
    [Fact]
    public async Task A_wallet_cannot_have_two_active_assignments()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync();

        Guid walletId;
        await using (var wallet = WalletContext())
        await using (var key = KeyContext())
        await using (var merchant = MerchantContext())
        {
            walletId = (await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct)).Value.WalletId;
        }

        await using (var context = WalletContext())
        {
            var ex = await Should.ThrowAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO wallet.WalletAssignment (Id, WalletId, MerchantId, Status, AssignedAt)
                VALUES (NEWID(), {0}, {1}, 'Active', SYSDATETIMEOFFSET())
                """,
                [walletId, merchantId], Ct));

            ex.Message.ShouldContain("IX_WalletAssignment_WalletId");
        }
    }

    [Fact]
    public async Task A_released_assignment_frees_the_wallet_for_a_new_active_one()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync();

        Guid walletId;
        await using (var wallet = WalletContext())
        await using (var key = KeyContext())
        await using (var merchant = MerchantContext())
        {
            walletId = (await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct)).Value.WalletId;
        }

        await using (var context = WalletContext())
        {
            var wallet = await context.Wallets.Include(w => w.Assignments).SingleAsync(w => w.Id == walletId, Ct);
            wallet.ReleaseAssignment(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        // A second Active assignment is now permitted (the earlier one is Released).
        await using (var context = WalletContext())
        {
            await Should.NotThrowAsync(() => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO wallet.WalletAssignment (Id, WalletId, MerchantId, Status, AssignedAt)
                VALUES (NEWID(), {0}, {1}, 'Active', SYSDATETIMEOFFSET())
                """,
                [walletId, merchantId], Ct));
        }
    }

    // ── Cross-module directory ────────────────────────────────────────────────

    [Fact]
    public async Task Directory_answers_whose_address_this_is_for_the_deposit_watcher()
    {
        var merchantId = await SeedActiveMerchantAsync();
        await SeedTronDepositHdWalletAsync();

        await using (var wallet = WalletContext())
        await using (var key = KeyContext())
        await using (var merchant = MerchantContext())
        {
            await NewService(wallet, key, merchant).ProvisionDepositAddressAsync(merchantId, Chain.Tron, Ct);
        }

        await using (var context = WalletContext())
        {
            var ownership = await new WalletDirectory(context).FindByAddressAsync(Chain.Tron, ExpectedFirstTronAddress, Ct);

            ownership.ShouldNotBeNull();
            ownership.MerchantId.ShouldBe(merchantId);
            ownership.WalletType.ShouldBe("Deposit");
            ownership.IsActive.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Directory_returns_null_for_an_unknown_address()
    {
        await using var context = WalletContext();
        (await new WalletDirectory(context).FindByAddressAsync(Chain.Tron, "TNobody", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_rowversion_guards_a_wallet_update()
    {
        var derivedKeyId = Guid.CreateVersion7();
        Guid walletId;
        await using (var context = WalletContext())
        {
            var w = WalletEntity.CreatePlatform(derivedKeyId, Chain.Tron, "TConc", WalletType.Treasury).Value;
            context.Wallets.Add(w);
            await context.SaveChangesAsync(Ct);
            walletId = w.Id;
        }

        await using var first = WalletContext();
        await using var second = WalletContext();
        var a = await first.Wallets.SingleAsync(w => w.Id == walletId, Ct);
        var b = await second.Wallets.SingleAsync(w => w.Id == walletId, Ct);

        a.Disable(DateTimeOffset.UtcNow);
        await first.SaveChangesAsync(Ct);

        b.Disable(DateTimeOffset.UtcNow);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }
}
