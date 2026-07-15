using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// The shared HMAC construction for the partner's request/callback signing scheme, kept in one place so
/// inbound verification and outbound signing can never drift apart. The signing secret is 64 hex chars,
/// hex-decoded to a 32-byte key — matching the partner's existing SDKs byte-for-byte.
/// </summary>
internal static class MerchantHmac
{
    /// <summary>Lower-case hex <c>HMAC-SHA256(hexDecode(secret), message)</c>.</summary>
    public static string ComputeHex(string signingSecretHex, string message)
    {
        var key = Convert.FromHexString(signingSecretHex);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Constant-time compare of a computed hex digest against a caller-supplied one; false on any malformed input.</summary>
    public static bool FixedTimeEqualsHex(string expectedHex, string providedHex)
    {
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Convert.FromHexString(expectedHex);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
