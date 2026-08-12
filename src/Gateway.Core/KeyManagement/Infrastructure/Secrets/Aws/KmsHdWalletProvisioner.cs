using System.Security.Cryptography;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets.Aws;

/// <summary>
/// PRODUCTION <see cref="IHdWalletProvisioner"/>: mints a genuinely random HD-wallet seed, seals it under the
/// purpose's KMS CMK, and stores <b>only</b> the ciphertext plus the public xpub (§10). Returns an un-persisted
/// <see cref="HdWallet"/> whose secret and public-key references both point at that one material row, so the
/// seed↔xpub pairing is arbitrated atomically. Seed generation is exactly-once: the store's unique reference
/// index means a concurrent first-provisioning (or a retry) adopts the existing seed instead of minting a
/// second. The seed exists only briefly in memory here and is wiped after encryption.
/// </summary>
public sealed class KmsHdWalletProvisioner(
    ISecretMaterialStore store,
    IAmazonKeyManagementService kms,
    IOptions<AwsKmsKeyCustodyOptions> options,
    TimeProvider timeProvider) : IHdWalletProvisioner
{
    private readonly AwsKmsKeyCustodyOptions _options = options.Value;

    public async Task<Result<HdWallet>> ProvisionMerchantDepositWalletAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default)
    {
        if (merchantId == Guid.Empty)
            return Result.Failure<HdWallet>(KeyManagementErrors.MerchantRequired);

        if (DerivationPath.SchemeFor(chain) != DerivationScheme.Bip32Secp256k1)
            return Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported); // ed25519 can't derive watch-only (§8)

        var reference = $"kms:material:merchant:{merchantId:N}:{chain}";
        var material = await EnsureMaterialAsync(reference, HdWalletPurpose.Deposit, chain, cancellationToken);
        if (material.IsFailure)
            return Result.Failure<HdWallet>(material.Error!);

        return HdWallet.CreateMerchantDeposit(
            merchantId, $"merchant-{merchantId:N}-{chain}-deposit", chain,
            SecretProviderKind.AwsKmsEnvelope, reference, reference, AccountPath(chain), timeProvider: timeProvider);
    }

    public async Task<Result<HdWallet>> ProvisionPlatformWithdrawalWalletAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        if (DerivationPath.SchemeFor(chain) != DerivationScheme.Bip32Secp256k1)
            return Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported);

        var reference = $"kms:material:platform:withdrawal:{chain}";
        var material = await EnsureMaterialAsync(reference, HdWalletPurpose.Withdrawal, chain, cancellationToken);
        if (material.IsFailure)
            return Result.Failure<HdWallet>(material.Error!);

        return HdWallet.Create(
            $"platform-{chain}-withdrawal", chain, HdWalletPurpose.Withdrawal,
            SecretProviderKind.AwsKmsEnvelope, reference, reference, AccountPath(chain),
            description: "Platform withdrawal HD wallet (hot pool seed, KMS-sealed)", timeProvider: timeProvider);
    }

    /// <summary>
    /// Ensures the KMS material for <paramref name="reference"/> exists and returns its account xpub. Idempotent:
    /// if the row already exists (retry or lost race) its stored xpub is returned and no new seed is minted.
    /// </summary>
    private async Task<Result<string>> EnsureMaterialAsync(
        string reference, HdWalletPurpose purpose, Chain chain, CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(reference, cancellationToken);
        if (existing is not null)
            return Result.Success(existing.Xpub);

        var keyArn = _options.KeyArnFor(purpose);
        if (string.IsNullOrWhiteSpace(keyArn))
            return Result.Failure<string>(KeyManagementErrors.SecretReferenceRequired); // no CMK configured for this purpose

        // A true-random master seed. 64 bytes = a full BIP-32 seed. Present only in this scope, wiped below.
        var seed = RandomNumberGenerator.GetBytes(64);
        try
        {
            var accountXpub = DeriveAccountXpub(seed, chain);
            var ciphertext = await EncryptSeedAsync(seed, keyArn!, reference, purpose, chain, cancellationToken);

            // Adopt-on-conflict: the winner's material (its seed + xpub) is what everyone uses, so the wallet
            // row this method's caller commits always pairs with a seed that can actually sign for its addresses.
            var stored = await store.GetOrAddAsync(
                new StoredSecretMaterial(reference, ciphertext, accountXpub, keyArn!, purpose, chain), cancellationToken);

            return Result.Success(stored.Xpub);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private async Task<byte[]> EncryptSeedAsync(
        byte[] seed, string keyArn, string reference, HdWalletPurpose purpose, Chain chain, CancellationToken cancellationToken)
    {
        using var plaintext = new MemoryStream(seed, writable: false);
        var response = await kms.EncryptAsync(new EncryptRequest
        {
            KeyId = keyArn,
            Plaintext = plaintext,
            EncryptionContext = KmsEnvelopeSecretProvider.EncryptionContext(reference, purpose, chain),
        }, cancellationToken);

        return response.CiphertextBlob.ToArray();
    }

    /// <summary>The change-level account xpub at <c>m/44'/{coin}'/0'/0</c> — the same node the child private
    /// keys derive under, so watch-only address derivation and signing agree.</summary>
    private static string DeriveAccountXpub(byte[] seed, Chain chain)
    {
        var coin = DerivationPath.CoinTypeFor(chain);
        return new ExtKey(Encoders.Hex.EncodeData(seed))
            .Derive(KeyPath.Parse($"44'/{coin}'/0'/0")).Neuter().ToString(Network.Main);
    }

    private static string AccountPath(Chain chain) => $"m/44'/{DerivationPath.CoinTypeFor(chain)}'/0'/0";
}
