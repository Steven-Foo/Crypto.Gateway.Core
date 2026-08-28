using System.Linq;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Nethereum.Util;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// address = last 20 bytes of keccak256(uncompressed public key without its 0x04 prefix),
/// rendered with an EIP-55 mixed-case checksum.
/// </summary>
public sealed class EthereumAddressEncoder : IAddressEncoder
{
    public Chain Chain => Chain.Ethereum;

    public string Encode(ReadOnlySpan<byte> publicKey)
    {
        var hash = Secp256k1PublicKey.Keccak20(publicKey);
        return ToEip55(Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>
    /// Structural check: "0x" + 40 hex digits. A mixed-case address must additionally match the EIP-55
    /// checksum (a typo flips a hex digit's case roughly as often as its value, so the checksum catches
    /// many of those); an all-lowercase or all-uppercase address is valid but uncheckummed, per EIP-55.
    /// </summary>
    public bool IsValidAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        var hex = address[2..];
        if (hex.Length != 40 || !hex.All(Uri.IsHexDigit))
            return false;

        var lower = hex.ToLowerInvariant();
        var isUncheckummed = hex == lower || hex == hex.ToUpperInvariant();
        return isUncheckummed || ToEip55(lower) == address;
    }

    /// <summary>
    /// EIP-55: uppercase hex digit <c>i</c> when nibble <c>i</c> of keccak256(lowercase address)
    /// is >= 8. A plain lowercase address is still valid; the mixed case is a typo checksum.
    /// </summary>
    private static string ToEip55(string lowercaseHex)
    {
        var hashOfAddress = new Sha3Keccack().CalculateHash(lowercaseHex);
        var result = new char[lowercaseHex.Length];

        for (var i = 0; i < lowercaseHex.Length; i++)
        {
            var c = lowercaseHex[i];
            var nibble = Convert.ToInt32(hashOfAddress[i].ToString(), 16);
            result[i] = char.IsAsciiLetter(c) && nibble >= 8 ? char.ToUpperInvariant(c) : c;
        }

        return "0x" + new string(result);
    }
}
