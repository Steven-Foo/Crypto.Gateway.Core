using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Domain;

public enum AssetStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// Chain metadata for a unit of account. <see cref="Decimals"/> is a *display* concern applied
/// only at the API edge — every amount in the system is an integer base unit (§14). Other modules
/// hold an opaque <c>AssetId</c> and never interpret the chain, which is what keeps Ledger
/// chain-agnostic.
/// </summary>
public sealed class Asset : Entity<Guid>
{
    private Asset(
        Guid id,
        Chain chain,
        string symbol,
        string? contractAddress,
        int decimals,
        AssetStatus status,
        DateTimeOffset createdAt) : base(id)
    {
        Chain = chain;
        Symbol = symbol;
        ContractAddress = contractAddress;
        Decimals = decimals;
        Status = status;
        CreatedAt = createdAt;
    }

    private Asset() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public string Symbol { get; private set; } = null!;

    /// <summary>Null for the chain's native coin (TRX, ETH, SOL).</summary>
    public string? ContractAddress { get; private set; }

    public int Decimals { get; private set; }
    public AssetStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsNative => ContractAddress is null;

    public static Result<Asset> Create(
        Chain chain,
        string symbol,
        string? contractAddress,
        int decimals,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Result.Failure<Asset>(Error.Validation("asset.symbol_required", "Asset symbol is required."));

        if (decimals is < 0 or > 38)
            return Result.Failure<Asset>(Error.Validation("asset.decimals_out_of_range", "Decimals must be between 0 and 38."));

        return Result.Success(new Asset(
            Guid.CreateVersion7(),
            chain,
            symbol.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(contractAddress) ? null : contractAddress.Trim(),
            decimals,
            AssetStatus.Active,
            (timeProvider ?? TimeProvider.System).GetUtcNow()));
    }

    public void Disable() => Status = AssetStatus.Disabled;

    public void Enable() => Status = AssetStatus.Active;
}
