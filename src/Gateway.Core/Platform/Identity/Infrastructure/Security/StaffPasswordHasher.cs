using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256, hand-rolled on top of <see cref="Rfc2898DeriveBytes"/> (BCL only — no new package, same
/// preference the existing <c>HmacApiSecretHasher</c> shows). Stored as <c>{iterations}.{saltBase64}.{hashBase64}</c>
/// so the work factor can be raised later without invalidating hashes created under a lower one.
/// </summary>
public sealed class StaffPasswordHasher : IStaffPasswordHasher
{
    private const int Iterations = 210_000; // OWASP 2023 baseline for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
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
