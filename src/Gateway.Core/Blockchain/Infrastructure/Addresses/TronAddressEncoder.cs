using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// TRON reuses Ethereum's keccak-20 account hash, prefixes it with 0x41 (mainnet), and renders it
/// as Base58Check. Every mainnet address therefore begins with 'T'.
/// </summary>
public sealed class TronAddressEncoder : IAddressEncoder
{
    public const byte MainnetPrefix = 0x41;

    public Chain Chain => Chain.Tron;

    public string Encode(ReadOnlySpan<byte> publicKey)
    {
        var hash = Secp256k1PublicKey.Keccak20(publicKey);

        Span<byte> raw = stackalloc byte[21];
        raw[0] = MainnetPrefix;
        hash.CopyTo(raw[1..]);

        return Base58.EncodeCheck(raw);
    }

    /// <summary>The 21-byte 0x41-prefixed form used by TRON's RPC and smart contracts.</summary>
    public static byte[] ToRawAddress(string base58Address)
    {
        var raw = Base58.DecodeCheck(base58Address);

        if (raw.Length != 21 || raw[0] != MainnetPrefix)
            throw new FormatException("Not a TRON mainnet address (expected 21 bytes prefixed 0x41).");

        return raw;
    }
}
