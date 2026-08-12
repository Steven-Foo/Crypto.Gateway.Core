namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier config for seeding the platform hot withdrawal <b>pool</b>. Bound from <c>Treasury</c>.
/// Each entry asks the seeder to ensure that many hot wallets (watch-only children of the one platform
/// withdrawal HD wallet) exist for a chain. Holds no key material and no addresses (§10) — the addresses are
/// <em>derived</em> from the withdrawal seed at seed time, not configured.
/// </summary>
public sealed class TreasuryDevHotWalletOptions
{
    public const string SectionName = "Treasury";

    public List<HotWalletPoolSeed> HotWalletPool { get; init; } = [];
}

public sealed class HotWalletPoolSeed
{
    public string Chain { get; init; } = null!;

    /// <summary>How many hot wallets the pool should hold for this chain. The seeder is grow-only.</summary>
    public int Size { get; init; }
}
