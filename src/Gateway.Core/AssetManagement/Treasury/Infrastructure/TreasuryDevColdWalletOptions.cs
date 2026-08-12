namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier config for seeding the cold treasury address(es). Bound from <c>Treasury</c>. Holds only
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

    /// <summary>The cold treasury address (public). No key material here.</summary>
    public string Address { get; init; } = null!;
}
