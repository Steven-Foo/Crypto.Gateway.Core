using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// The SAME production custody path as <see cref="KmsEnvelopeCustodyTests"/>, but against a REAL AWS KMS
/// endpoint instead of a fake — the live go-live gate for withdrawal/sweep money-out. It exercises the real
/// <see cref="KmsHdWalletProvisioner"/> (mint seed → <c>kms:Encrypt</c>) and <see cref="KmsEnvelopeSecretProvider"/>
/// (<c>kms:Decrypt</c> → derive child key); only the DB material store is in-memory. Passing proves the CMKs,
/// the IAM/key-policy grant, the SDK credentials, and the encryption-context (AAD) binding all work end-to-end.
///
/// <para><b>Skipped unless configured.</b> Set <c>CPE_KMS_REGION</c>, <c>CPE_KMS_DEPOSIT_ARN</c>,
/// <c>CPE_KMS_WITHDRAWAL_ARN</c>, and provide AWS credentials via the default chain (an SSO profile, or
/// <c>AWS_ACCESS_KEY_ID</c>/<c>AWS_SECRET_ACCESS_KEY</c>/<c>AWS_SESSION_TOKEN</c> env vars) for a principal
/// holding <c>kms:Encrypt</c>+<c>kms:Decrypt</c> on both CMKs. Never commit credentials (§10).</para>
/// </summary>
public sealed class KmsEnvelopeLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record LiveConfig(string Region, string DepositArn, string WithdrawalArn);

    private static LiveConfig Require()
    {
        var region = Environment.GetEnvironmentVariable("CPE_KMS_REGION");
        var deposit = Environment.GetEnvironmentVariable("CPE_KMS_DEPOSIT_ARN");
        var withdrawal = Environment.GetEnvironmentVariable("CPE_KMS_WITHDRAWAL_ARN");

        if (region is not { Length: > 0 } || deposit is not { Length: > 0 } || withdrawal is not { Length: > 0 })
            Assert.Skip("Set CPE_KMS_REGION / CPE_KMS_DEPOSIT_ARN / CPE_KMS_WITHDRAWAL_ARN + AWS creds to run the live KMS round-trip.");

        return new LiveConfig(region!, deposit!, withdrawal!);
    }

    private static (KmsHdWalletProvisioner Provisioner, KmsEnvelopeSecretProvider Provider, InMemoryMaterialStore Store, IAmazonKeyManagementService Kms) Compose(LiveConfig cfg)
    {
        var options = Options.Create(new AwsKmsKeyCustodyOptions
        {
            Enabled = true,
            Region = cfg.Region,
            KeyArns = new AwsKmsKeyCustodyOptions.KmsKeyArns { Deposit = cfg.DepositArn, Withdrawal = cfg.WithdrawalArn },
        });

        // Credentials come from the default AWS chain — never from config (§10). Region is the only identifier.
        IAmazonKeyManagementService kms = new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(cfg.Region));
        var store = new InMemoryMaterialStore();

        var provisioner = new KmsHdWalletProvisioner(store, kms, options, TimeProvider.System);

        // The provider is a singleton that reads its store through a scope; give it a real scope factory.
        var services = new ServiceCollection();
        services.AddScoped<ISecretMaterialStore>(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var provider = new KmsEnvelopeSecretProvider(
            scopeFactory, kms, options, NullLogger<KmsEnvelopeSecretProvider>.Instance);

        return (provisioner, provider, store, kms);
    }

    [Fact]
    public async Task Live_withdrawal_wallet_seals_and_the_derived_signing_key_matches_the_watch_only_address()
    {
        var cfg = Require();
        var (provisioner, provider, store, kms) = Compose(cfg);
        using var _ = kms;

        var wallet = (await provisioner.ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;
        store.Items[wallet.SecretReference].KmsKeyId.ShouldBe(cfg.WithdrawalArn); // withdrawal purpose → withdrawal CMK

        var reference = wallet.SecretReference;

        using var xpubLease = await provider.GetAsync(reference, Ct); // no '#': public xpub read, no decrypt
        var xpub = xpubLease.AsPublicUtf8String();
        xpub.ShouldBe(store.Items[reference].Xpub);

        const long index = 7;
        var watchOnlyPublicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(xpub, index);

        // "{ref}#{index}" ⇒ real kms:Decrypt the seed in memory, derive the child PRIVATE key.
        using var keyLease = await provider.GetAsync($"{reference}#{index}", Ct);
        var childPrivateKey = keyLease.Value.ToArray();
        childPrivateKey.Length.ShouldBe(32);

        var signingPublicKey = new Key(childPrivateKey).PubKey.Decompress().ToBytes();

        // The money-critical invariant, now proven through live KMS: the key that signs == the key behind the
        // watch-only address derived from the public xpub.
        signingPublicKey.ShouldBe(watchOnlyPublicKey);
    }

    [Fact]
    public async Task Live_deposit_wallet_seals_under_the_deposit_cmk_and_derives_consistently()
    {
        var cfg = Require();
        var (provisioner, provider, store, kms) = Compose(cfg);
        using var _ = kms;

        var merchantId = Guid.CreateVersion7();
        var wallet = (await provisioner.ProvisionMerchantDepositWalletAsync(merchantId, Chain.Tron, Ct)).Value;
        store.Items[wallet.SecretReference].KmsKeyId.ShouldBe(cfg.DepositArn); // deposit purpose → deposit CMK

        using var xpubLease = await provider.GetAsync(wallet.SecretReference, Ct);
        var xpub = xpubLease.AsPublicUtf8String();

        using var keyLease = await provider.GetAsync($"{wallet.SecretReference}#0", Ct);
        var signingPublicKey = new Key(keyLease.Value.ToArray()).PubKey.Decompress().ToBytes();

        signingPublicKey.ShouldBe(new Bip32Secp256k1KeyDeriver().DerivePublicKey(xpub, 0));
    }

    [Fact]
    public async Task Live_decrypt_rejects_a_tampered_encryption_context()
    {
        var cfg = Require();
        var (provisioner, provider, store, kms) = Compose(cfg);
        using var _ = kms;

        var wallet = (await provisioner.ProvisionPlatformWithdrawalWalletAsync(Chain.Tron, Ct)).Value;
        var reference = wallet.SecretReference;

        // Rebind the row to a different chain: the provider reconstructs the encryption context with the tampered
        // chain, which no longer matches what was sealed — real KMS refuses (AAD integrity, §10).
        store.Items[reference] = store.Items[reference] with { Chain = Chain.Ethereum };

        await Should.ThrowAsync<InvalidCiphertextException>(async () =>
        {
            using var _ = await provider.GetAsync($"{reference}#0", Ct);
        });
    }

    /// <summary>In-memory <see cref="ISecretMaterialStore"/> with insert-once/adopt-on-conflict semantics.</summary>
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
}
