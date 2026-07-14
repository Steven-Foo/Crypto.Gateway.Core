using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using NBitcoin;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;

/// <summary>
/// BIP-32 <c>CKDpub</c> over secp256k1: child public key = f(account xpub, index), with no seed and
/// no private key anywhere in the process. Serves Tron and Ethereum.
///
/// Deriving a hardened child from an xpub is mathematically impossible; NBitcoin enforces that, and
/// <c>Bip32DerivationTests</c> asserts it. That impossibility is precisely why Solana (ed25519,
/// hardened-only) cannot use this deriver.
/// </summary>
public sealed class Bip32Secp256k1KeyDeriver : IWatchOnlyKeyDeriver
{
    public DerivationScheme Scheme => DerivationScheme.Bip32Secp256k1;

    /// <param name="accountPublicKey">Base58 xpub at the branch level, e.g. <c>m/44'/195'/0'/0</c>.</param>
    /// <param name="index">Non-hardened child index, 0 .. 2^31-1.</param>
    /// <returns>The 65-byte uncompressed public key, as the address encoders expect.</returns>
    public byte[] DerivePublicKey(string accountPublicKey, long index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountPublicKey);

        if (!DerivationPath.IsIndexInRange(index))
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Index must be between 0 and {DerivationPath.MaxIndex} (non-hardened range).");
        }

        var extPubKey = ExtPubKey.Parse(accountPublicKey, Network.Main);
        var child = extPubKey.Derive((uint)index);

        return child.PubKey.Decompress().ToBytes();
    }
}

public sealed class KeyDeriverFactory : IKeyDeriverFactory
{
    private readonly Dictionary<DerivationScheme, IWatchOnlyKeyDeriver> _watchOnly;

    public KeyDeriverFactory(IEnumerable<IWatchOnlyKeyDeriver> derivers) =>
        _watchOnly = derivers.ToDictionary(d => d.Scheme);

    public bool SupportsWatchOnly(DerivationScheme scheme) => _watchOnly.ContainsKey(scheme);

    public IWatchOnlyKeyDeriver WatchOnlyFor(DerivationScheme scheme) =>
        _watchOnly.TryGetValue(scheme, out var deriver)
            ? deriver
            : throw new InvalidOperationException(
                $"{scheme} does not support watch-only derivation; it requires seed access.");
}
