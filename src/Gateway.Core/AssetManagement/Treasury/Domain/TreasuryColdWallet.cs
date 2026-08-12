using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

/// <summary>
/// The platform's cold treasury wallet for a chain: a watch-only address whose key is NOT held by the system
/// (a human signs outbound transfers client-side). It is the sweep destination and the reload source. Watch-only
/// by nature — the system never derives from it or signs with it — so it is homed here in Treasury rather than
/// forced into the Wallet module's key-bearing platform-wallet model (§10).
/// </summary>
public sealed class TreasuryColdWallet : Entity<Guid>
{
    private TreasuryColdWallet(Guid id, Chain chain, string address, DateTimeOffset now) : base(id)
    {
        Chain = chain;
        Address = address;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private TreasuryColdWallet() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TreasuryColdWallet> Register(Chain chain, string address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<TreasuryColdWallet>(TreasuryReloadErrors.AddressRequired);

        return Result.Success(new TreasuryColdWallet(Guid.CreateVersion7(), chain, address.Trim(), now));
    }

    /// <summary>Re-points the cold wallet at a new address (an ops correction). Idempotent for the same address.</summary>
    public void UpdateAddress(string address, DateTimeOffset now)
    {
        Address = address.Trim();
        UpdatedAt = now;
    }
}
