using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// A platform signing key that was imported directly (no seed, no xpub) — the dev/testnet hot wallet.
/// The dangerous property these tests protect: such a wallet must never have a child address derived
/// from it, because there is no real derivation lineage and the result would be a bogus, unfunded
/// address that looks legitimate (§14). Runs against a real SQL Server: the idempotency and the
/// no-derive guard both hinge on database state and constraints.
/// </summary>
public sealed class PlatformKeyRegistrationTests : IAsyncLifetime
{
    private const string DbName = "CpeKeyManagementPlatformKeyTests";
    private const string HotAddress = "TAueoxR1rwogpLDjYJzB7GGYYWgPbtajSs";
    private const string OtherAddress = "TRNFXxfaRuyynzoiZRkdQgP5xodKPXakDd";
    private const string SecretReference = "tron-hot-wallet-0";
    private const string PrivateKeyHex = "7f931ff72f6b1b0e5671158d952b53a28a4fb5d448cd95062596d22a2b47c607";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static KeyManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<KeyManagementDbContext>().UseSqlServer(ConnectionString).Options);

    private static PlatformKeyRegistrationService NewRegistrar(KeyManagementDbContext context)
    {
        var secretProvider = InMemorySecretProvider.FromStrings(
            new Dictionary<string, string> { [SecretReference] = PrivateKeyHex });

        return new PlatformKeyRegistrationService(new HdWalletRepository(context), secretProvider, TimeProvider.System);
    }

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

    [Fact]
    public async Task Registering_an_imported_key_creates_an_imported_wallet_with_no_public_key()
    {
        await using var context = NewContext();
        var result = await NewRegistrar(context).RegisterImportedKeyAsync(
            Chain.Tron, DerivationPurpose.Withdrawal, HotAddress, SecretReference, "hot wallet", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(HotAddress);
        result.Value.KeyReference.ShouldBe(SecretReference);

        await using var verify = NewContext();
        var wallet = await verify.HdWallets.SingleAsync(Ct);
        wallet.IsImported.ShouldBeTrue();
        wallet.PublicKeyReference.ShouldBeNull();
        wallet.Purpose.ShouldBe(HdWalletPurpose.Withdrawal);
        wallet.MerchantId.ShouldBeNull();

        var derivedKey = await verify.DerivedKeys.SingleAsync(Ct);
        derivedKey.Address.ShouldBe(HotAddress);
        derivedKey.DerivationIndex.ShouldBe(0);
    }

    [Fact]
    public async Task Re_registering_the_same_address_is_idempotent()
    {
        await using (var context = NewContext())
        {
            (await NewRegistrar(context).RegisterImportedKeyAsync(
                Chain.Tron, DerivationPurpose.Withdrawal, HotAddress, SecretReference, cancellationToken: Ct))
                .IsSuccess.ShouldBeTrue();
        }

        Guid secondDerivedKeyId;
        await using (var context = NewContext())
        {
            var second = await NewRegistrar(context).RegisterImportedKeyAsync(
                Chain.Tron, DerivationPurpose.Withdrawal, HotAddress, SecretReference, cancellationToken: Ct);
            second.IsSuccess.ShouldBeTrue();
            secondDerivedKeyId = second.Value.DerivedKeyId;
        }

        await using var verify = NewContext();
        (await verify.HdWallets.CountAsync(Ct)).ShouldBe(1);
        (await verify.DerivedKeys.CountAsync(Ct)).ShouldBe(1);
        (await verify.DerivedKeys.SingleAsync(Ct)).Id.ShouldBe(secondDerivedKeyId);
    }

    [Fact]
    public async Task Registering_a_different_address_for_the_same_chain_and_purpose_is_a_conflict()
    {
        await using (var context = NewContext())
        {
            (await NewRegistrar(context).RegisterImportedKeyAsync(
                Chain.Tron, DerivationPurpose.Withdrawal, HotAddress, SecretReference, cancellationToken: Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await using var conflicting = NewContext();
        var result = await NewRegistrar(conflicting).RegisterImportedKeyAsync(
            Chain.Tron, DerivationPurpose.Withdrawal, OtherAddress, SecretReference, cancellationToken: Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(KeyManagementErrors.PlatformKeyAddressConflict.Code);
    }

    /// <summary>
    /// THE regression test. An imported wallet has no derivable children — allocation must refuse rather
    /// than encode a bogus address from a non-existent xpub (§14). Exercised through the real
    /// <see cref="WalletDerivationService"/> path a future Sweep/Reconciliation caller would use.
    /// </summary>
    [Fact]
    public async Task Allocating_a_child_from_an_imported_wallet_is_refused()
    {
        await using (var context = NewContext())
        {
            (await NewRegistrar(context).RegisterImportedKeyAsync(
                Chain.Tron, DerivationPurpose.Withdrawal, HotAddress, SecretReference, cancellationToken: Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await using var context2 = NewContext();
        var derivation = new WalletDerivationService(
            new HdWalletRepository(context2),
            new KeyDeriverFactory([new Bip32Secp256k1KeyDeriver()]),
            new AddressEncoderFactory([new TronAddressEncoder()]),
            new SecretProviderFactory([InMemorySecretProvider.FromStrings(
                new Dictionary<string, string> { [SecretReference] = PrivateKeyHex })]),
            TimeProvider.System,
            []);

        var result = await derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(KeyManagementErrors.ImportedKeyCannotDerive.Code);

        // And no phantom DerivedKey was recorded.
        await using var verify = NewContext();
        (await verify.DerivedKeys.CountAsync(Ct)).ShouldBe(1); // only the index-0 registration key
    }
}
