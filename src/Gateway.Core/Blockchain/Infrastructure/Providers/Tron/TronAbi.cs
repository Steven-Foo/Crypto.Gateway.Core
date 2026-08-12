using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// ABI encoding for TRON smart-contract calls. Money-critical and pure (unit-tested against a fixed
/// vector): a wrong recipient or amount here sends funds to the wrong address or moves the wrong value.
/// </summary>
public static class TronAbi
{
    /// <summary>
    /// Encodes the arguments of <c>transfer(address,uint256)</c> as a hex string (no <c>0x</c>): the
    /// recipient right-aligned in a 32-byte word, followed by the amount as a 32-byte big-endian
    /// <c>uint256</c>. The node prepends the 4-byte selector itself from <c>function_selector</c>.
    /// </summary>
    /// <param name="toBase58Address">Recipient TRON address (Base58Check, <c>T…</c>).</param>
    /// <param name="amount">Transfer amount in the token's base units (§14).</param>
    public static string EncodeTransfer(string toBase58Address, BigInteger amount)
    {
        if (amount < BigInteger.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Transfer amount cannot be negative.");

        // A TRON/EVM address is the low 20 bytes of a 32-byte ABI word (12 leading zero bytes).
        var toWord = new string('0', 24) + TronAddress.ToEvmHex(toBase58Address); // 24 + 40 = 64 hex chars

        var magnitude = amount.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (magnitude.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(amount), "Transfer amount exceeds uint256 (32 bytes).");

        Span<byte> amountWord = stackalloc byte[32];
        magnitude.CopyTo(amountWord[(32 - magnitude.Length)..]);

        return toWord + Convert.ToHexString(amountWord).ToLowerInvariant();
    }

    /// <summary>The 4-byte selector of <c>balanceOf(address)</c> = keccak256("balanceOf(address)")[..4].</summary>
    public const string BalanceOfSelector = "70a08231";

    /// <summary>
    /// Encodes a full <c>eth_call</c> data payload for <c>balanceOf(address)</c>: the 4-byte selector
    /// followed by the owner address right-aligned in a 32-byte word, as a <c>0x</c>-hex string. Unlike
    /// the native <c>triggersmartcontract</c> path (where the node prepends the selector), an
    /// <c>eth_call</c> supplies the complete calldata, so the selector is included here.
    /// </summary>
    /// <param name="ownerBase58Address">Account whose token balance to read (Base58Check, <c>T…</c>).</param>
    public static string EncodeBalanceOf(string ownerBase58Address)
    {
        var ownerWord = new string('0', 24) + TronAddress.ToEvmHex(ownerBase58Address); // 24 + 40 = 64 hex chars
        return "0x" + BalanceOfSelector + ownerWord;
    }
}
