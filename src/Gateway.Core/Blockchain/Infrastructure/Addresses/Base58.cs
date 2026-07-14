using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// Bitcoin-alphabet Base58, and Base58Check (payload ‖ first 4 bytes of double-SHA256).
/// Solana uses plain Base58 with no checksum; TRON uses Base58Check.
/// Verified against the published USDT-TRC20 contract address in <c>AddressEncoderTests</c>.
/// </summary>
public static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var value = BigInteger.Zero;
        foreach (var b in data)
        {
            value = (value << 8) | b;
        }

        var builder = new StringBuilder();
        while (value > BigInteger.Zero)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            builder.Insert(0, Alphabet[(int)remainder]);
        }

        // Each leading zero byte is a literal '1' — this is why a 32-zero-byte Solana key
        // encodes as thirty-two '1's rather than the empty string.
        foreach (var b in data)
        {
            if (b != 0)
                break;

            builder.Insert(0, '1');
        }

        return builder.ToString();
    }

    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        var big = BigInteger.Zero;
        foreach (var c in value)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
                throw new FormatException($"'{c}' is not a Base58 character.");

            big = big * 58 + index;
        }

        var bytes = big.IsZero ? [] : big.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = 0;
        while (leadingZeros < value.Length && value[leadingZeros] == '1')
        {
            leadingZeros++;
        }

        var result = new byte[leadingZeros + bytes.Length];
        bytes.CopyTo(result.AsSpan(leadingZeros));
        return result;
    }

    public static string EncodeCheck(ReadOnlySpan<byte> payload)
    {
        Span<byte> full = stackalloc byte[payload.Length + 4];
        payload.CopyTo(full);
        Checksum(payload).CopyTo(full[payload.Length..]);
        return Encode(full);
    }

    public static byte[] DecodeCheck(string value)
    {
        var full = Decode(value);
        if (full.Length < 5)
            throw new FormatException("Base58Check value is too short to contain a checksum.");

        var payload = full.AsSpan(0, full.Length - 4);
        var expected = Checksum(payload);

        if (!CryptographicOperations.FixedTimeEquals(full.AsSpan(full.Length - 4), expected))
            throw new FormatException("Base58Check checksum mismatch.");

        return payload.ToArray();
    }

    private static byte[] Checksum(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(SHA256.HashData(payload))[..4];
}
