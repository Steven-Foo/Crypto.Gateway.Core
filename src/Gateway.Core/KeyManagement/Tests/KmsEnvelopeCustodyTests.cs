using System.Text;
using System.Text.Json;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// The production KMS-envelope custody. These prove the two properties that make it safe: (1) the private key
/// the signer derives from a KMS-sealed seed matches, exactly, the address watch-only derivation produced from
/// the public xpub — so we never sign for the wrong address (§14); and (2) the §10 protections hold — the
/// encryption context binds a ciphertext to its wallet, an unconfigured KMS key is refused, and a KMS wallet
/// can never resolve to an in-memory provider.
/// </summary>
public sealed class KmsEnvelopeCustodyTests
{
    private const string DepositArn = "arn:aws:kms:ap-southeast-1:111:key/deposit";
    private const string WithdrawalArn = "arn:aws:kms:ap-southeast-1:111:key/withdrawal";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryMaterialStore _store = new();
    private readonly IAmazonKeyManagementService _kms = FakeKms();

    private static AwsKmsKeyCustodyOptions Options() => new()
    {
        Enabled = true,
        Region = "ap-southeast-1",
        KeyArns = new AwsKmsKeyCustodyOptions.KmsKeyArns { Deposit = DepositArn, Withdrawal = WithdrawalArn },
    };

    private KmsHdWalletProvisioner Provisioner() =>
        new(_store, _kms, Microsoft.Extensions.Options.Options.Create(Options()), TimeProvider.System);

    private KmsEnvelopeSecretProvider Provider()
    {
        // The provider is a singleton that reads its store through a scope; give it a real scope factory.
        var services = new ServiceCollection();
        services.AddScoped<ISecretMaterialStore>(_ => _store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new KmsEnvelopeSecretProvider(
            scopeFactory, _kms, Microsoft.Extensions.Options.Options.Create(Options()),
            NullLogger<KmsEnvelopeSecretProvider>.Instance);
    }

    [Fact]
    public async Task The_signing_key_derived_from_the_sealed_seed_matches_the_watch_only_address()
    {
        var wallet = (await Provisioner().ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;

        wallet.SecretProvider.ShouldBe(SecretProviderKind.AwsKmsEnvelope);
        wallet.IsImported.ShouldBeFalse();
        // One material row backs both references, so the seed↔xpub pairing is atomic.
        wallet.SecretReference.ShouldBe(wallet.PublicKeyReference);
        _store.Items.ShouldHaveSingleItem();

        var reference = wallet.SecretReference;

        // The public xpub read (no '#') returns exactly what was stored.
        using var xpubLease = await Provider().GetAsync(reference, Ct);
        var xpub = xpubLease.AsPublicUtf8String();
        xpub.ShouldBe(_store.Items[reference].Xpub);

        const long index = 7;
        var watchOnlyPublicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(xpub, index);

        // The signing reference ("{ref}#{index}") decrypts the seed and derives the child PRIVATE key.
        using var keyLease = await Provider().GetAsync($"{reference}#{index}", Ct);
        var childPrivateKey = keyLease.Value.ToArray();
        childPrivateKey.Length.ShouldBe(32);

        var signingPublicKey = new Key(childPrivateKey).PubKey.Decompress().ToBytes();

        // The money-critical invariant: the key that signs == the key behind the watch-only address.
        signingPublicKey.ShouldBe(watchOnlyPublicKey);
    }

    [Fact]
    public async Task A_merchant_deposit_wallet_seals_under_the_deposit_cmk_and_derives_consistently()
    {
        var merchantId = Guid.CreateVersion7();
        var wallet = (await Provisioner().ProvisionMerchantDepositWalletAsync(merchantId, Chain.Tron, Ct)).Value;

        wallet.MerchantId.ShouldBe(merchantId);
        _store.Items[wallet.SecretReference].KmsKeyId.ShouldBe(DepositArn); // deposit purpose → deposit CMK

        using var xpubLease = await Provider().GetAsync(wallet.SecretReference, Ct);
        var xpub = xpubLease.AsPublicUtf8String();

        using var keyLease = await Provider().GetAsync($"{wallet.SecretReference}#0", Ct);
        var signingPublicKey = new Key(keyLease.Value.ToArray()).PubKey.Decompress().ToBytes();

        signingPublicKey.ShouldBe(new Bip32Secp256k1KeyDeriver().DerivePublicKey(xpub, 0));
    }

    [Fact]
    public async Task Provisioning_the_same_wallet_twice_mints_the_seed_only_once()
    {
        var provisioner = Provisioner();
        var first = (await provisioner.ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;
        var second = (await provisioner.ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;

        first.SecretReference.ShouldBe(second.SecretReference);
        _store.Items.ShouldHaveSingleItem();                       // one seed, not two
        await _kms.Received(1).EncryptAsync(Arg.Any<EncryptRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_tampered_encryption_context_makes_the_decrypt_fail()
    {
        var wallet = (await Provisioner().ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;
        var reference = wallet.SecretReference;
        var original = _store.Items[reference];

        // Rebind the row to a different chain: the provider will reconstruct the encryption context with the
        // tampered chain, which no longer matches what was sealed — KMS refuses (AAD integrity).
        _store.Items[reference] = original with { Chain = Chain.Ethereum };

        await Should.ThrowAsync<InvalidCiphertextException>(async () =>
        {
            using var _ = await Provider().GetAsync($"{reference}#0", Ct);
        });
    }

    [Fact]
    public async Task An_unconfigured_kms_key_is_refused_before_any_decrypt()
    {
        const string reference = "kms:material:platform:withdrawal:Tron";
        // A row that names a key this system was never configured to trust (e.g. tampered in the DB).
        _store.Items[reference] = new StoredSecretMaterial(
            reference, [1, 2, 3], "xpubDummy", "arn:aws:kms:elsewhere:999:key/attacker",
            HdWalletPurpose.Withdrawal, Chain.Tron);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var _ = await Provider().GetAsync($"{reference}#0", Ct);
        });
        ex.Message.ShouldContain("not a configured CMK");

        // The guard fires before KMS is ever contacted.
        await _kms.DidNotReceive().DecryptAsync(Arg.Any<DecryptRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_kms_wallet_can_never_resolve_to_an_in_memory_provider_and_vice_versa()
    {
        Provider().Kind.ShouldBe(SecretProviderKind.AwsKmsEnvelope);

        var dev = InMemorySecretProvider.FromStrings(new Dictionary<string, string> { ["x"] = "y" });

        // A dev-only composition cannot serve a KMS wallet…
        new SecretProviderFactory([dev]).Supports(SecretProviderKind.AwsKmsEnvelope).ShouldBeFalse();

        // …and a production KMS composition cannot serve an in-memory wallet (§10 interlock, both directions).
        var kmsFactory = new SecretProviderFactory([Provider()]);
        kmsFactory.Supports(SecretProviderKind.InMemoryDevelopment).ShouldBeFalse();
        kmsFactory.Supports(SecretProviderKind.AwsKmsEnvelope).ShouldBeTrue();
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    /// <summary>An in-memory <see cref="ISecretMaterialStore"/> with insert-once/adopt-on-conflict semantics.</summary>
    private sealed class InMemoryMaterialStore : ISecretMaterialStore
    {
        public readonly Dictionary<string, StoredSecretMaterial> Items = new();

        public Task<StoredSecretMaterial?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(reference));

        public Task<StoredSecretMaterial> GetOrAddAsync(StoredSecretMaterial material, CancellationToken cancellationToken = default)
        {
            if (Items.TryGetValue(material.Reference, out var existing))
                return Task.FromResult(existing);

            Items[material.Reference] = material;
            return Task.FromResult(material);
        }
    }

    /// <summary>
    /// A faithful KMS fake: envelope encrypt/decrypt that enforces the two guarantees the real service does —
    /// the key id must match, and the encryption context (AAD) must match — so the security tests are real.
    /// </summary>
    private static IAmazonKeyManagementService FakeKms()
    {
        var kms = Substitute.For<IAmazonKeyManagementService>();

        kms.EncryptAsync(Arg.Any<EncryptRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<EncryptRequest>();
                var blob = new Blob(req.KeyId, Canonical(req.EncryptionContext), Convert.ToBase64String(req.Plaintext.ToArray()));
                return Task.FromResult(new EncryptResponse
                {
                    KeyId = req.KeyId,
                    CiphertextBlob = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blob))),
                });
            });

        kms.DecryptAsync(Arg.Any<DecryptRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<DecryptRequest>();
                var blob = JsonSerializer.Deserialize<Blob>(Encoding.UTF8.GetString(req.CiphertextBlob.ToArray()))!;

                if (!string.IsNullOrEmpty(req.KeyId) && req.KeyId != blob.KeyId)
                    throw new IncorrectKeyException("The ciphertext refers to a different KMS key.");

                if (Canonical(req.EncryptionContext) != blob.Context)
                    throw new InvalidCiphertextException("Encryption context does not match.");

                return Task.FromResult(new DecryptResponse
                {
                    KeyId = blob.KeyId,
                    Plaintext = new MemoryStream(Convert.FromBase64String(blob.Plaintext)),
                });
            });

        return kms;
    }

    private static string Canonical(IDictionary<string, string>? context) =>
        context is null
            ? string.Empty
            : string.Join(";", context.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    private sealed record Blob(string KeyId, string Context, string Plaintext);
}
