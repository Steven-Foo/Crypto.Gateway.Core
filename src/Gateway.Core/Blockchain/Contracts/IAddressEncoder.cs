using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;

/// <summary>
/// Encodes a <em>public</em> key into a chain's address format. This is the address-encoding half of
/// §8's <c>IAddressDeriver</c>; the BIP-32/SLIP-10 half lives in KeyManagement, because it touches
/// key material and must never pass through the chain-integration module (§10).
///
/// A public key is public data, so nothing sensitive crosses this boundary.
/// </summary>
public interface IAddressEncoder
{
    Chain Chain { get; }

    /// <summary>
    /// Chain-specific input:
    /// secp256k1 chains (Tron, Ethereum) take the <b>65-byte uncompressed</b> public key (0x04 prefix);
    /// Solana takes the <b>32-byte ed25519</b> public key.
    /// </summary>
    string Encode(ReadOnlySpan<byte> publicKey);
}

public interface IAddressEncoderFactory
{
    bool Supports(Chain chain);

    /// <summary>Throws for an unsupported chain — callers should ask <see cref="Supports"/> first.</summary>
    IAddressEncoder For(Chain chain);
}
