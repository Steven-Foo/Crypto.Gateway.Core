using System.Security.Cryptography;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// The one PBKDF2-SHA256 implementation for human-chosen passwords, shared by every identity module so the
/// scheme cannot drift between copies (the same reason <see cref="AmountConversion"/> lives here). BCL only —
/// no new package, matching the preference the existing secret hashers show.
///
/// <para>This is a cross-cutting <b>primitive</b>, not business logic (§4.8): it encodes no rule about who may
/// log in, what a session is, or which tenant owns it — each identity module keeps its own port
/// (<c>IStaffPasswordHasher</c> / <c>IMerchantPasswordHasher</c>) and decides how to use this.</para>
///
/// <para>Deliberately NOT for machine-generated secrets: a high-entropy API secret or session token is served
/// by a fast keyed/unkeyed hash (see <see cref="OpaqueToken"/> and Merchant's <c>HmacApiSecretHasher</c>).
/// A human password has far less entropy, so it needs a slow, salted hash — same job, different threat model.</para>
///
/// Stored as <c>{iterations}.{saltBase64}.{hashBase64}</c>, so the work factor can be raised later without
/// invalidating hashes created under a lower one.
/// </summary>
public static class Pbkdf2PasswordHash
{
    private const int Iterations = 210_000; // OWASP 2023 baseline for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifies a password against a stored hash, re-deriving with that hash's own recorded iteration
    /// count. Malformed input returns false rather than throwing. Comparison is constant-time.</summary>
    public static bool Verify(string password, string hash)
    {
        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
