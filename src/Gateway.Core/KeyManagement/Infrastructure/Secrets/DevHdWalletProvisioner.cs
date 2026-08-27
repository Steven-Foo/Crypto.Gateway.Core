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
/// no seed is ever persisted (§10); the fake signer needs no key. Real signing over these wallets is served
/// by <see cref="DevHdWalletSigningSecretProvider"/>, which re-derives the same seed one step further, only
/// at the moment of signing.
///
/// The seed is derived deterministically (<see cref="DevHdWalletDeterministicSeed"/>) from a fixed dev
/// constant and the merchant id, so a merchant's dev addresses are reproducible across runs and in tests,
/// while remaining distinct <em>per merchant</em> (the property the separate-seed custody model exists to
/// give). This is DEV entropy only — production mints a true-random seed inside a KMS/HSM behind the same
/// <see cref="IHdWalletProvisioner"/> port (deferred).
///
/// Because the xpub is a pure, reproducible function of (merchant id, chain) — or (chain) alone for the
/// platform pool — <see cref="ResolveMerchantDepositXpub"/>/<see cref="ResolvePlatformWithdrawalXpub"/> are
/// exposed so <c>DevHdWalletReseeder</c> can recompute the identical value for an <em>already-existing</em>
/// wallet row after a restart wipes <see cref="MutableInMemorySecretStore"/>, without minting a new wallet.
/// </summary>
public sealed class DevHdWalletProvisioner(
    MutableInMemorySecretStore secrets, TimeProvider timeProvider, IOptions<DevelopmentKeyCustodyOptions> options)
    : IHdWalletProvisioner
{
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
        var accountXpub = ResolveMerchantDepositXpub(merchantId, chain);

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

    public Task<Result<HdWallet>> ProvisionPlatformWithdrawalWalletAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        // Only secp256k1 chains derive addresses watch-only from an xpub; ed25519 (Solana) can't (§8).
        if (DerivationPath.SchemeFor(chain) != DerivationScheme.Bip32Secp256k1)
            return Task.FromResult(Result.Failure<HdWallet>(KeyManagementErrors.SchemeNotSupported));

        var coin = DerivationPath.CoinTypeFor(chain);
        var accountPath = $"m/44'/{coin}'/0'/0"; // change-level xpub → CKDpub derives the pool's child addresses
        var accountXpub = ResolvePlatformWithdrawalXpub(chain);

        var xpubReference = $"dev:hdwallet:platform:withdrawal:{chain}:xpub";
        var seedReference = $"dev:hdwallet:platform:withdrawal:{chain}:seed"; // a label only; no seed is stored (§10)
        secrets.Put(xpubReference, accountXpub);

        var wallet = HdWallet.Create(
            $"platform-{chain}-withdrawal", chain, HdWalletPurpose.Withdrawal,
            SecretProviderKind.InMemoryDevelopment, seedReference, xpubReference, accountPath,
            description: "Platform withdrawal HD wallet (hot pool seed)", timeProvider: timeProvider);

        return Task.FromResult(wallet);
    }

    /// <summary>
    /// The account xpub a merchant's deposit wallet uses (or would use, if minted right now): a configured
    /// override when set, else the deterministic dev-salt derivation. Always the same result for the same
    /// (merchantId, chain) — that reproducibility is what lets a restart recover without re-minting.
    /// </summary>
    public string ResolveMerchantDepositXpub(Guid merchantId, Chain chain)
    {
        var coin = DerivationPath.CoinTypeFor(chain);
        var configuredXpub = options.Value.DevMerchantXpub;

        // Prefer a configured real xpub (recoverable, private — the developer's own wallet tree) over the
        // throwaway public-salt seed. REQUIRED before sending real mainnet funds (the salt is public here).
        return !string.IsNullOrWhiteSpace(configuredXpub)
            ? configuredXpub.Trim()
            : new ExtKey(Encoders.Hex.EncodeData(DevHdWalletDeterministicSeed.ForMerchant(merchantId)))
                .Derive(KeyPath.Parse($"44'/{coin}'/0'/0")).Neuter().ToString(Network.Main);
    }

    /// <summary>The platform withdrawal pool's account xpub — same reproducibility property as above, keyed by chain only.</summary>
    public string ResolvePlatformWithdrawalXpub(Chain chain)
    {
        var coin = DerivationPath.CoinTypeFor(chain);
        var configuredXpub = options.Value.DevWithdrawalXpub;

        // Prefer a configured real xpub (recoverable, private — the operator's own withdrawal tree) over the
        // throwaway public-salt seed. REQUIRED before sending real mainnet funds (the salt is public here).
        return !string.IsNullOrWhiteSpace(configuredXpub)
            ? configuredXpub.Trim()
            : new ExtKey(Encoders.Hex.EncodeData(DevHdWalletDeterministicSeed.ForPlatformWithdrawal(chain)))
                .Derive(KeyPath.Parse($"44'/{coin}'/0'/0")).Neuter().ToString(Network.Main);
    }
}
