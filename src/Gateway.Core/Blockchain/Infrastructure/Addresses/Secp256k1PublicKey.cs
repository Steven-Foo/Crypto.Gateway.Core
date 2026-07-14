using Nethereum.Util;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

internal static class Secp256k1PublicKey
{
    public const int UncompressedLength = 65;
    private const byte UncompressedPrefix = 0x04;

    /// <summary>
    /// The 20-byte account hash shared by Ethereum and TRON: keccak256 of the uncompressed public
    /// key with its 0x04 prefix stripped, truncated to the trailing 20 bytes.
    /// </summary>
    public static byte[] Keccak20(ReadOnlySpan<byte> uncompressedPublicKey)
    {
        if (uncompressedPublicKey.Length != UncompressedLength || uncompressedPublicKey[0] != UncompressedPrefix)
        {
            throw new ArgumentException(
                $"Expected a {UncompressedLength}-byte uncompressed secp256k1 public key beginning with 0x04, " +
                $"got {uncompressedPublicKey.Length} bytes.",
                nameof(uncompressedPublicKey));
        }

        var hash = new Sha3Keccack().CalculateHash(uncompressedPublicKey[1..].ToArray());
        return hash[^20..];
    }
}
