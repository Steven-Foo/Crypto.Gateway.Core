using System.Security.Cryptography;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// A generated one-time password for a newly-created or password-reset account, shared by every identity
/// module so the entropy cannot drift between copies (same reasoning as <see cref="OpaqueToken"/>).
///
/// <para>18 bytes from a CSPRNG, URL-safe base64 — ~24 characters: high entropy, yet short enough to read
/// aloud or copy-paste out of a terminal/JSON response without escaping issues. Deliberately shorter than an
/// <see cref="OpaqueToken"/> (which is a machine credential no human ever retypes).</para>
///
/// <para>The value is shown to the operator once, at creation, and stored only as a
/// <see cref="Pbkdf2PasswordHash"/> — it is a password, not a token, so it gets the slow hash.</para>
/// </summary>
public static class TemporaryPassword
{
    private const int EntropyBytes = 18;

    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(EntropyBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
