using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. Mints a merchant's HD wallet with its own seed, exports the BIP-44 account
/// xpub, and stores <em>only that public key</em> in the writable dev store — matching the dev model where
/// no seed is ever persisted (§10); the fake signer needs no key, and real dev signing is deferred.
///
/// The seed is derived deterministically from a fixed dev constant and the merchant id, so a merchant's dev
/// addresses are reproducible across runs and in tests, while remaining distinct <em>per merchant</em> (the
/// property the separate-seed custody model exists to give). This is DEV entropy only — production mints a
/// true-random seed inside a KMS/HSM behind the same <see cref="IHdWalletProvisioner"/> port (deferred).
/// </summary>
public sealed class DevHdWalletProvisioner(
    MutableInMemorySecretStore secrets, TimeProvider timeProvider, IOptions<DevelopmentKeyCustodyOptions> options)
    : IHdWalletProvisioner
{
    // Not a production key or a real seed — a dev-only KDF salt that turns a merchant id into throwaway
    // deterministic entropy. Changing it re-derives all dev addresses. PUBLIC in this repo, so never for real funds.
    private const string DevMasterSalt = "cpe-dev-hdwallet-master-v1-not-for-production";

    public Task<Result<HdWallet>> ProvisionMerchantDepositWalletAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default)
    {
        if (merchantId == Guid.Empty)
            return Task.FromResult(Result.Failure<HdWallet>(KeyManagementErrors.MerchantRequired));

        // Only secp256k1 chains derive addresses watch-only from an xpub; ed25519 (Solana) can't (§8).
        if (DerivationPath.SchemeFor(chain) != DerivationScheme.Bip32Secp256k1)
            return Task.FromResult(Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported));

        var coin = DerivationPath.CoinTypeFor(chain);
        var accountPath = $"m/44'/{coin}'/0'/0"; // change-level xpub → CKDpub derives address children

        // Prefer a configured real xpub (recoverable, private — the developer's own wallet tree) over the
        // throwaway public-salt seed. REQUIRED before sending real mainnet funds (the salt is public here).
        var configuredXpub = options.Value.DevMerchantXpub;
        var accountXpub = !string.IsNullOrWhiteSpace(configuredXpub)
            ? configuredXpub.Trim()
            : new ExtKey(Encoders.Hex.EncodeData(DeriveSeed(merchantId)))
                .Derive(KeyPath.Parse($"44'/{coin}'/0'/0")).Neuter().ToString(Network.Main);

        // Deterministic references: on a create-on-first-use race, both callers write the same public key to
        // the same reference (idempotent) and only one wallet row wins the unique index — no orphaned secret.
        var xpubReference = $"dev:hdwallet:{merchantId:N}:{chain}:xpub";
        var seedReference = $"dev:hdwallet:{merchantId:N}:{chain}:seed"; // a label only; no seed is stored (§10)
        secrets.Put(xpubReference, accountXpub);

        var wallet = HdWallet.CreateMerchantDeposit(
            merchantId, $"merchant-{merchantId:N}-{chain}-deposit", chain,
            SecretProviderKind.InMemoryDevelopment, seedReference, xpubReference, accountPath, timeProvider: timeProvider);

        return Task.FromResult(wallet);
    }

    private static byte[] DeriveSeed(Guid merchantId)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(DevMasterSalt));
        return hmac.ComputeHash(merchantId.ToByteArray()); // 64 bytes → a valid BIP-32 master seed
    }
}
