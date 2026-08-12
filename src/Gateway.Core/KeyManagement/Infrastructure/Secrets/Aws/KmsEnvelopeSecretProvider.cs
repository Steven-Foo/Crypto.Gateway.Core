using System.Security.Cryptography;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;

/// <summary>
/// PRODUCTION custody. Resolves a reference to material without ever persisting or returning a seed (§10):
/// <list type="bullet">
///   <item>a plain reference (an xpub reference, no <c>#</c>) → the public account xpub, in the clear;</item>
///   <item>a signing reference <c>"{ref}#{index}"</c> → decrypts the KMS-sealed seed <b>in memory only</b>,
///   derives the secp256k1 child private key at that index, returns it as a zeroized
///   <see cref="SecretLease"/>, and wipes the seed immediately.</item>
/// </list>
/// Reports <see cref="SecretProviderKind.AwsKmsEnvelope"/>, so only an HD wallet configured for this provider
/// resolves to it — a KMS wallet can never fall back to an in-memory seed, and vice-versa (the §10 interlock).
///
/// <para>A singleton (the <see cref="ISecretProviderFactory"/> and <c>TronSigner</c> that consume it are
/// singletons), so its short DB read for the material row runs in a per-call scope; the KMS client and options
/// are themselves singletons.</para>
/// </summary>
public sealed class KmsEnvelopeSecretProvider(
    IServiceScopeFactory scopeFactory,
    IAmazonKeyManagementService kms,
    IOptions<AwsKmsKeyCustodyOptions> options,
    ILogger<KmsEnvelopeSecretProvider> logger) : ISecretProvider
{
    private readonly AwsKmsKeyCustodyOptions _options = options.Value;

    public SecretProviderKind Kind => SecretProviderKind.AwsKmsEnvelope;

    public async Task<SecretLease> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        // "{ref}#{index}" ⇒ a signing request (derive a child private key); otherwise ⇒ a public xpub read.
        var hash = reference.IndexOf('#');
        var baseReference = hash < 0 ? reference : reference[..hash];

        var material = await LoadAsync(baseReference, cancellationToken)
            ?? throw new KeyNotFoundException($"No KMS material is registered for reference '{baseReference}'.");

        if (hash < 0)
            return new SecretLease(Encoding.UTF8.GetBytes(material.Xpub)); // public xpub — safe as bytes

        if (!long.TryParse(reference.AsSpan(hash + 1), out var index) || index < 0)
            throw new FormatException($"Signing reference '{reference}' has no valid child index.");

        // Refuse to point kms:Decrypt at a key this system was not configured to trust — a defence against a
        // tampered material row that names an attacker-controlled key.
        if (!_options.IsConfiguredKey(material.KmsKeyId))
            throw new InvalidOperationException(
                $"Material '{baseReference}' names KMS key '{material.KmsKeyId}', which is not a configured CMK.");

        var seed = await DecryptSeedAsync(material, baseReference, cancellationToken);
        try
        {
            var childPrivateKey = DeriveChildPrivateKey(seed, material.Chain, index);
            return new SecretLease(childPrivateKey); // the caller's SecretLease wipes it after signing
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private async Task<StoredSecretMaterial?> LoadAsync(string reference, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecretMaterialStore>();
        return await store.GetAsync(reference, cancellationToken);
    }

    private async Task<byte[]> DecryptSeedAsync(
        StoredSecretMaterial material, string baseReference, CancellationToken cancellationToken)
    {
        using var ciphertext = new MemoryStream(material.Ciphertext, writable: false);
        var response = await kms.DecryptAsync(new DecryptRequest
        {
            CiphertextBlob = ciphertext,
            KeyId = material.KmsKeyId,
            // Bound at encrypt time — KMS refuses to decrypt if it differs, so one wallet's ciphertext can
            // never be decrypted as another's even by someone holding full KMS access (AAD integrity).
            EncryptionContext = EncryptionContext(baseReference, material.Purpose, material.Chain),
        }, cancellationToken);

        logger.LogDebug("Decrypted seed material for {Reference} via KMS.", baseReference);
        return response.Plaintext.ToArray();
    }

    /// <summary>
    /// The seed → secp256k1 child private key at <c>m/44'/{coin}'/0'/0/{index}</c>, identical to the path the
    /// account xpub was neutered at, so this key signs for exactly the address watch-only derivation produced.
    /// This is the <b>only</b> code in the system that turns a seed into a private key (§10).
    /// </summary>
    private static byte[] DeriveChildPrivateKey(byte[] seed, Chain chain, long index)
    {
        var coin = DerivationPath.CoinTypeFor(chain);
        var master = new ExtKey(Encoders.Hex.EncodeData(seed));
        var child = master.Derive(KeyPath.Parse($"44'/{coin}'/0'/0/{index}"));
        return child.PrivateKey.ToBytes(); // 32 bytes
    }

    internal static Dictionary<string, string> EncryptionContext(string reference, HdWalletPurpose purpose, Chain chain) =>
        new()
        {
            ["reference"] = reference,
            ["purpose"] = purpose.ToString(),
            ["chain"] = chain.ToString(),
        };
}
