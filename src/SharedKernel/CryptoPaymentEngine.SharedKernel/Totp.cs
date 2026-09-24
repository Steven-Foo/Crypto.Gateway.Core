using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// RFC 6238 time-based one-time passwords (TOTP) over RFC 4226 HOTP — the algorithm Google Authenticator,
/// Authy, 1Password and Microsoft Authenticator all implement, so a secret provisioned here works in any of
/// them.
///
/// <para>Hand-rolled over the BCL's <see cref="HMACSHA1"/> rather than taking a package: the whole algorithm
/// is an HMAC, a dynamic truncation and a modulo, and the RFC publishes official test vectors, so this is
/// <em>provable</em> rather than trusted (see <c>TotpTests</c>). Adding a dependency on the authentication
/// path for forty lines of arithmetic is the worse trade (§11).</para>
///
/// <para><b>SHA-1 is correct here and is not a weakness.</b> HOTP is specified over HMAC-SHA-1, and HMAC's
/// security does not rest on the collision resistance that SHA-1 lost. Every authenticator app defaults to
/// it; choosing SHA-256 would produce codes most scanners cannot reproduce.</para>
///
/// <para>A cross-cutting <b>primitive</b>, not business logic (§4.8): it knows nothing about accounts,
/// enrollment, lockout or which actions demand a code. Each identity module owns those and keeps its own
/// port over this, exactly as they do over <see cref="Pbkdf2PasswordHash"/> and <see cref="OpaqueToken"/>.</para>
/// </summary>
public static class Totp
{
    public const int DefaultDigits = 6;
    public const int DefaultPeriodSeconds = 30;

    /// <summary>160 bits — the RFC 4226 reference length, and what authenticator apps expect.</summary>
    public const int DefaultSecretBytes = 20;

    /// <summary>
    /// How many steps either side of the present one are accepted, to absorb clock drift between the server
    /// and the phone. One step (±30s) and no more: every extra step both lengthens the window in which an
    /// observed code remains usable and enlarges the space a brute-forcer covers per guess.
    /// </summary>
    public const int DefaultSkewSteps = 1;

    /// <summary>A fresh CSPRNG secret. Returned to the caller to be encrypted at rest — never stored raw.</summary>
    public static byte[] GenerateSecret(int bytes = DefaultSecretBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 16);
        return RandomNumberGenerator.GetBytes(bytes);
    }

    /// <summary>The counter value for an instant — Unix seconds divided by the period, per RFC 6238 §4.2.</summary>
    public static long StepAt(DateTimeOffset time, int periodSeconds = DefaultPeriodSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(periodSeconds, 0);
        return time.ToUnixTimeSeconds() / periodSeconds;
    }

    /// <summary>
    /// The code for one counter value: HMAC-SHA-1 of the big-endian counter, dynamically truncated per
    /// RFC 4226 §5.3, reduced mod 10^digits and zero-padded.
    /// </summary>
    public static string ComputeAt(ReadOnlySpan<byte> secret, long step, int digits = DefaultDigits)
    {
        if (secret.IsEmpty)
            throw new ArgumentException("A TOTP secret is required.", nameof(secret));

        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 8);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> mac = stackalloc byte[20]; // SHA-1 output
        HMACSHA1.HashData(secret, counter, mac);

        // Dynamic truncation: the low nibble of the last byte picks a 4-byte window; the top bit is masked
        // off so the result is a positive 31-bit integer regardless of platform signedness.
        var offset = mac[^1] & 0x0F;
        var binary =
            ((mac[offset] & 0x7F) << 24) |
            (mac[offset + 1] << 16) |
            (mac[offset + 2] << 8) |
            mac[offset + 3];

        return (binary % Pow10(digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Whether <paramref name="code"/> is a valid code for <paramref name="now"/>, within
    /// <paramref name="skewSteps"/> either side.
    ///
    /// <para><b>This verifies; it does not consume.</b> There is deliberately no single-use tracking in this
    /// platform (see <c>docs/two-factor-authentication.md</c> §6.1) — a valid code stays valid for its own
    /// window, so an operator working through a queue is not paced at one action per step. The cost is that
    /// an observed code is reusable for up to that window, which is why the code must never be logged.</para>
    ///
    /// <para>Every candidate step is evaluated and compared in constant time, and the loop does not
    /// short-circuit on a match, so neither the outcome nor <em>which</em> step matched is timing-visible.</para>
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> secret,
        string? code,
        DateTimeOffset now,
        int skewSteps = DefaultSkewSteps,
        int digits = DefaultDigits,
        int periodSeconds = DefaultPeriodSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skewSteps);

        if (!TryNormalize(code, digits, out var normalized))
            return false;

        var current = StepAt(now, periodSeconds);
        var matched = false;

        for (var offset = -skewSteps; offset <= skewSteps; offset++)
        {
            var candidate = ComputeAt(secret, current + offset, digits);
            // Bitwise-or rather than |=-with-break: no early exit, so the work done is the same either way.
            matched |= FixedTimeEquals(normalized, candidate);
        }

        return matched;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI an authenticator scans. The label is <c>{issuer}:{account}</c> and the
    /// issuer is ALSO repeated as a parameter — both halves are required for the app to display the account
    /// correctly, and omitting the parameter is the usual reason an entry shows up unlabelled.
    /// </summary>
    public static string ProvisioningUri(
        string issuer,
        string accountName,
        ReadOnlySpan<byte> secret,
        int digits = DefaultDigits,
        int periodSeconds = DefaultPeriodSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return $"otpauth://totp/{label}" +
               $"?secret={ToBase32(secret)}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={digits}&period={periodSeconds}";
    }

    // ── Base32 (RFC 4648), the encoding otpauth secrets use. Unpadded: authenticator apps accept it, and
    //    trailing '=' inside a URI query is a common source of scanner failures.

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
            builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);

        return builder.ToString();
    }

    /// <summary>Decodes an unpadded Base32 string. Whitespace and '=' padding are tolerated (people retype
    /// these by hand); any other character is a failure rather than a silently dropped byte.</summary>
    public static bool TryFromBase32(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var output = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in value)
        {
            if (c is ' ' or '-' or '=')
                continue;

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0)
                return false;

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        bytes = [.. output];
        return bytes.Length > 0;
    }

    private static bool TryNormalize(string? code, int digits, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        // Authenticators display codes grouped ("418 392") and people paste them that way.
        Span<char> buffer = stackalloc char[digits];
        var length = 0;

        foreach (var c in code)
        {
            if (c is ' ' or '-')
                continue;

            if (!char.IsAsciiDigit(c) || length == digits)
                return false;

            buffer[length++] = c;
        }

        if (length != digits)
            return false;

        normalized = new string(buffer);
        return true;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static int Pow10(int digits) => digits switch
    {
        6 => 1_000_000,
        7 => 10_000_000,
        8 => 100_000_000,
        _ => throw new ArgumentOutOfRangeException(nameof(digits)),
    };
}
