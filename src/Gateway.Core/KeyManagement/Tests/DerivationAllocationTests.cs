using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Allocation is the point where a bug becomes an irreversible loss: two merchants handed the same
/// deposit address means every payment to it is misattributed, and no ledger entry can undo an
/// on-chain transfer. These tests run against a real SQL Server because the guarantee lives in the
/// database's row lock, not in application code.
/// </summary>
public sealed class DerivationAllocationTests : IAsyncLifetime
{
    private const string DbName = "CpeKeyManagementAllocationTests";
    private const string SecretReference = "arn:test:seed";
    private const string PublicKeyReference = "arn:test:xpub";
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static string TronBranchXpub =>
        ExtKey.CreateFromSeed(new Mnemonic(TestMnemonic, Wordlist.English).DeriveSeed())
            .Derive(new KeyPath("44'/195'/0'/0"))
            .Neuter()
            .ToString(Network.Main);

    private static KeyManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);

    private static WalletDerivationService NewService(KeyManagementDbContext context)
    {
        var secretProvider = InMemorySecretProvider.FromStrings(
            new Dictionary<string, string> { [PublicKeyReference] = TronBranchXpub });

        return new WalletDerivationService(
            new HdWalletRepository(context),
            new KeyDeriverFactory([new Bip32Secp256k1KeyDeriver()]),
            new AddressEncoderFactory([new TronAddressEncoder(), new EthereumAddressEncoder()]),
            new SecretProviderFactory([secretProvider]),
            TimeProvider.System,
            []);
    }

    private static HdWallet NewTronDepositWallet() =>
        HdWallet.Create(
            "TRON deposit pool", Chain.Tron, HdWalletPurpose.Deposit,
            SecretProviderKind.InMemoryDevelopment, SecretReference, PublicKeyReference,
            "m/44'/195'/0'/0").Value;

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    private static async Task<Guid> SeedWalletAsync()
    {
        await using var context = NewContext();
        var wallet = NewTronDepositWallet();
        context.HdWallets.Add(wallet);
        await context.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Allocating_derives_a_deterministic_address_and_records_the_key()
    {
        await SeedWalletAsync();

        await using var context = NewContext();
        var result = await NewService(context).AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DerivationIndex.ShouldBe(0);
        result.Value.DerivationPath.ShouldBe("m/44'/195'/0'/0/0");

        // The same published vector asserted in Bip32DerivationTests — end to end through the DB.
        result.Value.Address.ShouldBe("TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH");
    }

    [Fact]
    public async Task Sequential_allocations_hand_out_consecutive_indices()
    {
        await SeedWalletAsync();

        var indices = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            await using var context = NewContext();
            indices.Add((await NewService(context).AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct)).Value.DerivationIndex);
        }

        indices.ShouldBe([0, 1, 2, 3, 4]);
    }

    /// <summary>
    /// THE test. Thirty-two callers race for indices; every one must get a distinct index, and the
    /// derived addresses must all differ. A read-modify-write allocation fails this.
    /// </summary>
    [Fact]
    public async Task Concurrent_allocations_never_hand_out_the_same_index_twice()
    {
        const int concurrency = 32;
        await SeedWalletAsync();

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var context = NewContext();
            return await NewService(context).AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct);
        });

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.IsSuccess);

        var indices = results.Select(r => r.Value.DerivationIndex).ToList();
        indices.Distinct().Count().ShouldBe(concurrency);
        indices.Order().ShouldBe(Enumerable.Range(0, concurrency).Select(i => (long)i));

        var addresses = results.Select(r => r.Value.Address).ToList();
        addresses.Distinct().Count().ShouldBe(concurrency);

        await using var verify = NewContext();
        (await verify.DerivedKeys.CountAsync(Ct)).ShouldBe(concurrency);
        (await verify.HdWallets.SingleAsync(Ct)).NextDerivationIndex.ShouldBe(concurrency);
    }

    // ── Transactionality ──────────────────────────────────────────────────────

    /// <summary>
    /// A rolled-back operation must un-consume the index. Because custody owns DerivedKey, the
    /// allocation and the insert share one transaction — so a failure leaves neither a gap nor,
    /// far worse, an index whose address was already handed out.
    /// </summary>
    [Fact]
    public async Task A_failed_operation_rolls_the_index_back_leaving_no_gap()
    {
        var walletId = await SeedWalletAsync();

        await using (var context = NewContext())
        {
            var repository = new HdWalletRepository(context);

            var result = await repository.InTransactionAsync<long>(async ct =>
            {
                var allocated = await repository.AllocateNextIndexAsync(walletId, ct);
                allocated.Value.ShouldBe(0);

                return Result.Failure<long>(Error.Failure("test.boom", "simulated failure after allocation"));
            }, Ct);

            result.IsFailure.ShouldBeTrue();
        }

        await using (var context = NewContext())
        {
            (await context.HdWallets.SingleAsync(Ct)).NextDerivationIndex.ShouldBe(0);
        }

        // The next real allocation therefore receives index 0 — never reusing an issued address.
        await using (var context = NewContext())
        {
            var next = await NewService(context).AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct);
            next.Value.DerivationIndex.ShouldBe(0);
        }
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Allocation_stops_at_the_last_non_hardened_index()
    {
        var walletId = await SeedWalletAsync();

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE keymgmt.HdWallet SET NextDerivationIndex = {0} WHERE Id = {1}",
                [DerivationPath.MaxIndex, walletId], Ct);
        }

        await using (var context = NewContext())
        {
            var repository = new HdWalletRepository(context);

            // The final index is allocatable...
            var last = await repository.AllocateNextIndexAsync(walletId, Ct);
            last.Value.ShouldBe(DerivationPath.MaxIndex);

            // ...and the pool is then spent, rather than rolling into the hardened range.
            var beyond = await repository.AllocateNextIndexAsync(walletId, Ct);
            beyond.Error!.Code.ShouldBe(KeyManagementErrors.PoolExhausted.Code);
        }
    }

    [Fact]
    public async Task A_disabled_wallet_cannot_allocate()
    {
        var walletId = await SeedWalletAsync();

        await using (var context = NewContext())
        {
            var wallet = await context.HdWallets.SingleAsync(Ct);
            wallet.Disable(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var result = await new HdWalletRepository(context).AllocateNextIndexAsync(walletId, Ct);
            result.Error!.Code.ShouldBe(KeyManagementErrors.NotActive.Code);
        }
    }

    [Fact]
    public async Task Allocating_for_a_chain_with_no_active_wallet_fails_cleanly()
    {
        await SeedWalletAsync(); // TRON only

        await using var context = NewContext();
        var result = await NewService(context).AllocateNextAsync(Chain.Ethereum, DerivationPurpose.Deposit, Ct);

        result.Error!.Code.ShouldBe(KeyManagementErrors.NotFound.Code);
    }

    /// <summary>
    /// Solana wallets have no watch-only deriver registered, so allocation refuses rather than
    /// quietly reaching for the seed.
    /// </summary>
    [Fact]
    public async Task An_ed25519_wallet_refuses_watch_only_allocation()
    {
        await using (var context = NewContext())
        {
            context.HdWallets.Add(HdWallet.Create(
                "SOL deposit", Chain.Solana, HdWalletPurpose.Deposit,
                SecretProviderKind.InMemoryDevelopment, SecretReference, null, "m/44'/501'").Value);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var service = new WalletDerivationService(
                new HdWalletRepository(context),
                new KeyDeriverFactory([new Bip32Secp256k1KeyDeriver()]),
                new AddressEncoderFactory([new SolanaAddressEncoder()]),
                new SecretProviderFactory([InMemorySecretProvider.FromStrings(new Dictionary<string, string>())]),
                TimeProvider.System,
                []);

            var result = await service.AllocateNextAsync(Chain.Solana, DerivationPurpose.Deposit, Ct);

            result.Error!.Code.ShouldBe(KeyManagementErrors.SchemeNotSupported.Code);
        }
    }

    // ── Database-level invariants ─────────────────────────────────────────────

    [Fact]
    public async Task Two_active_wallets_for_the_same_chain_and_purpose_are_rejected()
    {
        await SeedWalletAsync();

        await using var context = NewContext();
        context.HdWallets.Add(NewTronDepositWallet());

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task An_archived_wallet_frees_the_chain_and_purpose_slot()
    {
        await SeedWalletAsync();

        await using (var context = NewContext())
        {
            (await context.HdWallets.SingleAsync(Ct)).Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            context.HdWallets.Add(NewTronDepositWallet());
            await Should.NotThrowAsync(() => context.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task The_same_index_cannot_be_recorded_twice_for_one_wallet()
    {
        var walletId = await SeedWalletAsync();

        await using var context = NewContext();
        var wallet = await context.HdWallets.SingleAsync(Ct);

        context.DerivedKeys.Add(wallet.DeriveKey(0, "TAddressOne", DateTimeOffset.UtcNow).Value);
        await context.SaveChangesAsync(Ct);

        context.DerivedKeys.Add(wallet.DeriveKey(0, "TAddressTwo", DateTimeOffset.UtcNow).Value);
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_same_address_cannot_be_recorded_twice_on_one_chain()
    {
        var walletId = await SeedWalletAsync();

        await using var context = NewContext();
        var wallet = await context.HdWallets.SingleAsync(Ct);

        context.DerivedKeys.Add(wallet.DeriveKey(0, "TSameAddress", DateTimeOffset.UtcNow).Value);
        await context.SaveChangesAsync(Ct);

        context.DerivedKeys.Add(wallet.DeriveKey(1, "TSameAddress", DateTimeOffset.UtcNow).Value);
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_derived_key_can_be_resolved_by_its_opaque_id()
    {
        await SeedWalletAsync();

        await using var context = NewContext();
        var service = NewService(context);
        var allocated = (await service.AllocateNextAsync(Chain.Tron, DerivationPurpose.Deposit, Ct)).Value;

        var found = await service.FindAsync(allocated.DerivedKeyId, Ct);

        found.ShouldNotBeNull();
        found.Address.ShouldBe(allocated.Address);
        found.DerivationIndex.ShouldBe(allocated.DerivationIndex);
    }

    /// <summary>Regression guard for §10: no column may ever hold key material.</summary>
    [Fact]
    public async Task No_column_in_the_keymgmt_schema_can_hold_key_material()
    {
        await using var context = NewContext();

        var offending = await context.Database.SqlQueryRaw<string>(
            """
            SELECT c.name AS Value
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'keymgmt'
              AND (c.name IN ('Mnemonic','Seed','PrivateKey','Secret','MasterSeed','WalletPassword')
                   OR c.name LIKE '%Mnemonic%' OR c.name LIKE '%PrivateKey%' OR c.name LIKE '%MasterSeed%')
            """).ToListAsync(Ct);

        offending.ShouldBeEmpty();
    }
}
