using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// The one generator for opaque, machine-generated credentials — session tokens and the per-session anti-CSRF
/// token — shared by every identity module so the entropy and hashing cannot drift between copies.
///
/// <para>256 bits of CSPRNG output, base64url-encoded. Because the value is high-entropy and machine-generated
/// (unlike a password), a fast unkeyed SHA-256 is sufficient to store it: only <see cref="Sha256Hex"/> of the
/// token is ever persisted, and the raw value is returned to the caller once, at issue time. For human-chosen
/// passwords use <see cref="Pbkdf2PasswordHash"/> instead.</para>
///
/// <para>A cross-cutting <b>primitive</b>, not business logic (§4.8): it knows nothing about sessions, tenants,
/// or expiry — the identity modules own those.</para>
/// </summary>
public static class OpaqueToken
{
    private const int TokenBytes = 32;

    /// <summary>A fresh 256-bit token, base64url-encoded (URL/header/cookie safe — no padding, no '+' or '/').</summary>
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Lower-case hex SHA-256 of a presented token — what gets stored and compared, never the raw token.</summary>
    public static string Sha256Hex(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
