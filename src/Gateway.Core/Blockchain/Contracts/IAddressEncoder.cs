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

    /// <summary>
    /// True when <paramref name="address"/> is a structurally well-formed address for this chain — correct
    /// format/length and, where the chain has one, a valid checksum. This is a format check only: it proves
    /// nothing about whether the address belongs to anyone, has ever been used, or is the address the caller
    /// actually meant to use — no software can tell "correct" apart from "a different, equally valid address"
    /// from the string alone. It exists to catch typos and malformed input before funds are ever reserved.
    /// </summary>
    bool IsValidAddress(string address);
}

public interface IAddressEncoderFactory
{
    bool Supports(Chain chain);

    /// <summary>Throws for an unsupported chain — callers should ask <see cref="Supports"/> first.</summary>
    IAddressEncoder For(Chain chain);
}
