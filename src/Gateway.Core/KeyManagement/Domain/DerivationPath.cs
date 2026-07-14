using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

/// <summary>
/// A validated BIP-44 derivation path, bound to a chain and a scheme.
///
/// Two things this type exists to make impossible:
/// <list type="bullet">
/// <item>An Ethereum HD wallet carrying TRON's coin type (or vice versa). Both are secp256k1, so
/// the wrong path silently produces valid-looking addresses whose funds we can never move.</item>
/// <item>A derivation index at or beyond 2^31, where BIP-32 reinterprets it as <em>hardened</em>
/// and yields a completely different key.</item>
/// </list>
/// </summary>
public sealed class DerivationPath : ValueObject
{
    /// <summary>Non-hardened BIP-32 indices occupy 0 .. 2^31-1; 2^31 and above mean hardened.</summary>
    public const long MaxIndex = int.MaxValue; // 2_147_483_647

    private const uint HardenedOffset = 0x8000_0000;

    private DerivationPath(string value, Chain chain, DerivationScheme scheme)
    {
        Value = value;
        Chain = chain;
        Scheme = scheme;
    }

    public string Value { get; }
    public Chain Chain { get; }
    public DerivationScheme Scheme { get; }

    /// <summary>SLIP-44 registered coin types. A mismatch here derives keys for the wrong chain.</summary>
    public static int CoinTypeFor(Chain chain) => chain switch
    {
        Chain.Ethereum => 60,
        Chain.Tron => 195,
        Chain.Solana => 501,
        _ => throw new ArgumentOutOfRangeException(nameof(chain), chain, "No SLIP-44 coin type is registered."),
    };

    public static DerivationScheme SchemeFor(Chain chain) => chain switch
    {
        Chain.Ethereum or Chain.Tron => DerivationScheme.Bip32Secp256k1,
        Chain.Solana => DerivationScheme.Slip10Ed25519,
        _ => throw new ArgumentOutOfRangeException(nameof(chain), chain, "No derivation scheme is registered."),
    };

    /// <summary>
    /// Expected shapes:
    /// <para><b>Bip32Secp256k1</b> — <c>m/44'/coin'/account'/change</c> (4 levels). The final level is
    /// non-hardened, so the xpub at this path can derive address children with <c>CKDpub</c>.</para>
    /// <para><b>Slip10Ed25519</b> — <c>m/44'/coin'</c> (2 levels). The address levels
    /// (<c>/index'/0'</c>) are hardened and appended at derivation time from the seed.</para>
    /// </summary>
    public static Result<DerivationPath> Create(string path, Chain chain)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<DerivationPath>(KeyManagementErrors.PathRequired);

        var trimmed = path.Trim();
        var segments = trimmed.Split('/');

        if (segments.Length < 2 || segments[0] != "m")
            return Result.Failure<DerivationPath>(KeyManagementErrors.PathMalformed);

        var levels = new List<(uint Index, bool Hardened)>();
        foreach (var segment in segments[1..])
        {
            if (!TryParseLevel(segment, out var level))
                return Result.Failure<DerivationPath>(KeyManagementErrors.PathMalformed);

            levels.Add(level);
        }

        if (levels[0] is not { Index: 44, Hardened: true })
            return Result.Failure<DerivationPath>(KeyManagementErrors.PathPurposeNotBip44);

        var scheme = SchemeFor(chain);

        var expectedLevels = scheme == DerivationScheme.Bip32Secp256k1 ? 4 : 2;
        if (levels.Count != expectedLevels)
            return Result.Failure<DerivationPath>(KeyManagementErrors.PathShapeInvalid);

        if (levels[1] is not { Hardened: true } coinLevel || coinLevel.Index != (uint)CoinTypeFor(chain))
            return Result.Failure<DerivationPath>(KeyManagementErrors.PathCoinTypeMismatch);

        if (scheme == DerivationScheme.Bip32Secp256k1)
        {
            // account must be hardened; change must NOT be, or CKDpub cannot derive addresses.
            if (!levels[2].Hardened || levels[3].Hardened)
                return Result.Failure<DerivationPath>(KeyManagementErrors.PathShapeInvalid);
        }

        return Result.Success(new DerivationPath(trimmed, chain, scheme));
    }

    /// <summary>
    /// Rehydrates a path that was already validated before it was persisted. Only persistence may
    /// call this — it deliberately skips validation, so passing unvalidated input here defeats the
    /// whole point of the type.
    /// </summary>
    internal static DerivationPath FromTrusted(string value, Chain chain, DerivationScheme scheme) =>
        new(value, chain, scheme);

    /// <summary>The full path of the address at <paramref name="index"/>, for audit and recovery.</summary>
    public Result<string> AddressPathFor(long index)
    {
        if (index is < 0 or > MaxIndex)
            return Result.Failure<string>(KeyManagementErrors.IndexOutOfRange);

        return Result.Success(Scheme == DerivationScheme.Bip32Secp256k1
            ? $"{Value}/{index}"          // m/44'/60'/0'/0/index
            : $"{Value}/{index}'/0'");    // m/44'/501'/index'/0'
    }

    public static bool IsIndexInRange(long index) => index is >= 0 and <= MaxIndex;

    public override string ToString() => Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
        yield return Chain;
        yield return Scheme;
    }

    private static bool TryParseLevel(string segment, out (uint Index, bool Hardened) level)
    {
        level = default;

        if (segment.Length == 0)
            return false;

        var hardened = segment[^1] is '\'' or 'h' or 'H';
        var digits = hardened ? segment[..^1] : segment;

        if (!uint.TryParse(digits, out var index) || index >= HardenedOffset)
            return false;

        level = (index, hardened);
        return true;
    }
}
