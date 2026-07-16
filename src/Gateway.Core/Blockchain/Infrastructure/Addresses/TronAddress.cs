namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

/// <summary>
/// Converts between TRON's Base58Check address (T…) and the 20-byte EVM-hex form its Ethereum-compatible
/// JSON-RPC returns. This is money-critical: a deposit is matched to a merchant by address, so a wrong
/// conversion credits the wrong account. TRON = <c>Base58Check(0x41 ‖ 20-byte hash)</c>; the JSON-RPC
/// hex form is that same 20-byte hash without the 0x41 prefix. Verified against the USDT-TRC20 contract.
/// </summary>
public static class TronAddress
{
    private const byte MainnetPrefix = 0x41;

    /// <summary>20-byte EVM hex (optionally <c>0x</c>-prefixed) → TRON Base58Check address.</summary>
    public static string FromEvmHex(ReadOnlySpan<char> hex)
    {
        hex = Strip0x(hex);
        if (hex.Length != 40)
            throw new FormatException($"Expected a 20-byte (40 hex char) address, got {hex.Length} chars.");

        Span<byte> raw = stackalloc byte[21];
        raw[0] = MainnetPrefix;
        Convert.FromHexString(hex).CopyTo(raw[1..]);
        return Base58.EncodeCheck(raw);
    }

    /// <summary>An ABI-encoded address topic (32-byte, left-padded) → TRON Base58Check address.</summary>
    public static string FromEvmTopic(ReadOnlySpan<char> topic)
    {
        topic = Strip0x(topic);
        if (topic.Length != 64)
            throw new FormatException($"Expected a 32-byte (64 hex char) topic, got {topic.Length} chars.");

        return FromEvmHex(topic[^40..]); // the low 20 bytes carry the address
    }

    /// <summary>TRON Base58Check address → 20-byte EVM hex (lowercase, no <c>0x</c>) for RPC filters.</summary>
    public static string ToEvmHex(string base58Address)
    {
        var raw = TronAddressEncoder.ToRawAddress(base58Address); // 21 bytes, 0x41-prefixed
        return Convert.ToHexString(raw.AsSpan(1)).ToLowerInvariant();
    }

    /// <summary>
    /// The 21-byte, already <c>0x41</c>-prefixed hex TRON's *native* wallet API returns for addresses
    /// (e.g. <c>to_address</c>/<c>owner_address</c> on a <c>TransferContract</c>) → TRON Base58Check.
    /// Distinct from <see cref="FromEvmHex"/>, which takes the 20-byte unprefixed form the Ethereum-
    /// compatible JSON-RPC uses — the native API already includes the version byte.
    /// </summary>
    public static string FromRawHex(ReadOnlySpan<char> hex)
    {
        hex = Strip0x(hex);
        if (hex.Length != 42)
            throw new FormatException($"Expected a 21-byte (42 hex char) raw TRON address, got {hex.Length} chars.");

        var raw = Convert.FromHexString(hex);
        if (raw[0] != MainnetPrefix)
            throw new FormatException("Not a TRON mainnet address (expected 0x41 prefix).");

        return Base58.EncodeCheck(raw);
    }

    private static ReadOnlySpan<char> Strip0x(ReadOnlySpan<char> hex)
    {
        hex = hex.Trim();
        return hex.StartsWith("0x") || hex.StartsWith("0X") ? hex[2..] : hex;
    }
}
