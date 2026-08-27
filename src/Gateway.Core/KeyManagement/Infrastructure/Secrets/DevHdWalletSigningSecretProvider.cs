using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. Serves real signing for the dev-tier's HD-derived watch-only wallets (a
/// merchant deposit wallet, or the platform withdrawal pool wallet) — the piece <see cref="DevHdWalletProvisioner"/>
/// deliberately never built, since it only ever needed the account xpub (§10: no seed is ever persisted).
///
/// <see cref="WalletDerivationService"/>/the platform-key directories resolve a signing reference as
/// <c>"{seedReference}#{index}"</c> (see <see cref="Application.PlatformSigningKeyDirectory"/> and
/// <see cref="Application.DepositSigningKeyDirectory"/>) — a shape the plain <see cref="MutableInMemorySecretStore"/>
/// was never able to answer, because it only ever held the public xpub under a DIFFERENT reference. This
/// provider recognises that composite shape and, only for it, re-derives the SAME deterministic seed
/// (<see cref="DevHdWalletDeterministicSeed"/>) one step further into the specific child's private key —
/// entirely in memory, only for the instant of signing, never written anywhere. Every other reference (an
/// xpub lookup, an imported platform key) passes straight through to the wrapped store, unchanged.
///
/// A wallet minted under a developer's own configured xpub override (<c>DevMerchantXpub</c>/
/// <c>DevWithdrawalXpub</c>) is deliberately NOT covered: its real private key belongs to the developer's own
/// external wallet, which this process never had and cannot reconstruct — signing for it correctly fails
/// with "no signing key" rather than silently guessing wrong.
/// </summary>
public sealed class DevHdWalletSigningSecretProvider(
    MutableInMemorySecretStore inner, IOptions<DevelopmentKeyCustodyOptions> options) : ISecretProvider
{
    private const string Prefix = "dev:hdwallet:";
    private const string PlatformWithdrawalInfix = "platform:withdrawal:";
    private const string SeedSuffix = ":seed";

    public SecretProviderKind Kind => inner.Kind;

    public Task<SecretLease> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        var privateKey = TryDeriveSigningKey(reference);
        return privateKey is null
            ? inner.GetAsync(reference, cancellationToken)
            : Task.FromResult(new SecretLease(privateKey));
    }

    private byte[]? TryDeriveSigningKey(string reference)
    {
        // Split "{seedReference}#{index}" — anything without a '#' is a plain reference (an xpub, or an
        // imported key), never a signing composite, so it always falls through untouched.
        var hashIndex = reference.LastIndexOf('#');
        if (hashIndex < 0 || !long.TryParse(reference[(hashIndex + 1)..], out var index) || index < 0)
            return null;

        var seedReference = reference[..hashIndex];
        if (!seedReference.StartsWith(Prefix, StringComparison.Ordinal) ||
            !seedReference.EndsWith(SeedSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var middle = seedReference[Prefix.Length..^SeedSuffix.Length]; // between "dev:hdwallet:" and ":seed"

        if (middle.StartsWith(PlatformWithdrawalInfix, StringComparison.Ordinal))
        {
            var chainText = middle[PlatformWithdrawalInfix.Length..];
            if (!Enum.TryParse<Chain>(chainText, ignoreCase: true, out var chain))
                return null;

            // A configured override xpub means the wallet's real key lives outside this process (the
            // developer's own wallet) — we never had it and must not guess. Let the caller see "not found".
            if (!string.IsNullOrWhiteSpace(options.Value.DevWithdrawalXpub))
                throw new KeyNotFoundException(
                    $"'{reference}' belongs to a configured DevWithdrawalXpub override — its private key is " +
                    "external and cannot be derived here.");

            return DeriveChildPrivateKey(DevHdWalletDeterministicSeed.ForPlatformWithdrawal(chain), chain, index);
        }

        // Otherwise the merchant shape: "{merchantId:N}:{chain}".
        var lastColon = middle.LastIndexOf(':');
        if (lastColon < 0)
            return null;

        var merchantIdText = middle[..lastColon];
        var merchantChainText = middle[(lastColon + 1)..];

        if (!Guid.TryParseExact(merchantIdText, "N", out var merchantId) ||
            !Enum.TryParse<Chain>(merchantChainText, ignoreCase: true, out var merchantChain))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.Value.DevMerchantXpub))
            throw new KeyNotFoundException(
                $"'{reference}' belongs to a configured DevMerchantXpub override — its private key is " +
                "external and cannot be derived here.");

        return DeriveChildPrivateKey(DevHdWalletDeterministicSeed.ForMerchant(merchantId), merchantChain, index);
    }

    /// <summary>
    /// Same account-level path as <see cref="DevHdWalletProvisioner"/> uses to export the xpub
    /// (<c>m/44'/coin'/0'/0</c>), continued one non-hardened step further to the specific child index —
    /// exactly mirroring <see cref="DerivationPath.AddressPathFor"/>, so the derived key always matches the
    /// address already on record for that index.
    /// </summary>
    private static byte[] DeriveChildPrivateKey(byte[] seed, Chain chain, long index)
    {
        var coin = DerivationPath.CoinTypeFor(chain);
        var accountKey = new ExtKey(Encoders.Hex.EncodeData(seed)).Derive(KeyPath.Parse($"44'/{coin}'/0'/0"));
        var childKey = accountKey.Derive((uint)index);
        return childKey.PrivateKey.ToBytes();
    }
}
