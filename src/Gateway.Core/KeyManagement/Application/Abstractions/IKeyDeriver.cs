using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

/// <summary>
/// Derives a child <em>public</em> key from an account public key (xpub), touching no secret at all.
///
/// Capability-segregated (§8): only schemes that actually support public derivation implement this.
/// ed25519/SLIP-0010 does not — it is hardened-only — so no ed25519 implementation exists, rather
/// than one that throws <c>NotSupportedException</c>. The absence <em>is</em> the contract.
/// </summary>
public interface IWatchOnlyKeyDeriver
{
    DerivationScheme Scheme { get; }

    /// <summary>
    /// Returns the public key in the form this chain's address encoder expects
    /// (65-byte uncompressed, for secp256k1).
    /// </summary>
    byte[] DerivePublicKey(string accountPublicKey, long index);
}

public interface IKeyDeriverFactory
{
    /// <summary>False for ed25519: those wallets need seed access, which this port never provides.</summary>
    bool SupportsWatchOnly(DerivationScheme scheme);

    IWatchOnlyKeyDeriver WatchOnlyFor(DerivationScheme scheme);
}
