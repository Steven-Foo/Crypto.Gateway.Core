namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier config for seeding the cold collection addresses. Bound from <c>Treasury</c>. Holds only
/// public, watch-only addresses — never a key (§10); the cold key stays with the human operator.
/// </summary>
public sealed class TreasuryDevColdWalletOptions
{
    public const string SectionName = "Treasury";

    public List<ColdWalletSeed> ColdWallets { get; init; } = [];
}

public sealed class ColdWalletSeed
{
    public string Chain { get; init; } = null!;

    /// <summary>The cold collection address (public). No key material here.</summary>
    public string Address { get; init; } = null!;

    /// <summary>Which class of swept funds it collects: <c>Safe</c> (default) or <c>Danger</c>. A dev
    /// environment that wants to exercise the segregation seeds one of each.</summary>
    public string? Kind { get; init; }

    /// <summary>Optional operator-facing name, carried through so a seeded environment reads like a real one.</summary>
    public string? Label { get; init; }
}
