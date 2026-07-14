using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;

/// <summary>
/// A blockchain address the platform controls. Every wallet is HD-derived — it holds an opaque
/// <see cref="DerivedKeyId"/> issued by the KeyManagement module, never the key, never the derivation
/// index. Only the public <see cref="Address"/> is copied here, so deposit scanning needs no
/// cross-module call.
///
/// Deposit wallets are dedicated: one address, one merchant, for life. Reassigning a deposit address
/// to a different merchant is forbidden — a late payment to the old address would be credited to the
/// wrong account.
/// </summary>
public sealed class Wallet : Entity<Guid>
{
    private readonly List<WalletAssignment> _assignments = [];

    private Wallet(
        Guid id,
        Guid derivedKeyId,
        Chain chain,
        string address,
        WalletType walletType,
        Guid? merchantId,
        string? description,
        DateTimeOffset createdAt) : base(id)
    {
        DerivedKeyId = derivedKeyId;
        Chain = chain;
        Address = address;
        WalletType = walletType;
        MerchantId = merchantId;
        Description = description;
        Status = WalletStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private Wallet() : base(Guid.Empty)
    {
    }

    public Guid DerivedKeyId { get; private set; }
    public Chain Chain { get; private set; }
    public string Address { get; private set; } = null!;
    public WalletType WalletType { get; private set; }

    /// <summary>Denormalised current holder — derived from the active assignment. Null for platform wallets.</summary>
    public Guid? MerchantId { get; private set; }

    public WalletStatus Status { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<WalletAssignment> Assignments => _assignments;

    public bool IsActive => Status == WalletStatus.Active;
    public bool IsMerchantAssignable => WalletType == WalletType.Deposit;

    public WalletAssignment? ActiveAssignment => _assignments.SingleOrDefault(a => a.IsActive);

    /// <summary>
    /// A dedicated deposit address for a merchant. Seeds its own active assignment, so the wallet and
    /// its assignment are always created and committed together.
    /// </summary>
    public static Result<Wallet> CreateDeposit(
        Guid derivedKeyId,
        Chain chain,
        string address,
        Guid merchantId,
        string? description = null,
        TimeProvider? timeProvider = null)
    {
        if (derivedKeyId == Guid.Empty)
            return Result.Failure<Wallet>(WalletErrors.DerivedKeyRequired);

        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<Wallet>(WalletErrors.AddressRequired);

        if (merchantId == Guid.Empty)
            return Result.Failure<Wallet>(WalletErrors.MerchantRequiredForDeposit);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var wallet = new Wallet(Guid.CreateVersion7(), derivedKeyId, chain, address.Trim(), WalletType.Deposit, merchantId, description, now);
        wallet._assignments.Add(WalletAssignment.Create(wallet.Id, merchantId, now));
        return Result.Success(wallet);
    }

    /// <summary>A platform wallet (treasury, hot, cold, energy). Never merchant-assigned.</summary>
    public static Result<Wallet> CreatePlatform(
        Guid derivedKeyId,
        Chain chain,
        string address,
        WalletType walletType,
        string? description = null,
        TimeProvider? timeProvider = null)
    {
        if (walletType == WalletType.Deposit)
            return Result.Failure<Wallet>(WalletErrors.MerchantRequiredForDeposit);

        if (derivedKeyId == Guid.Empty)
            return Result.Failure<Wallet>(WalletErrors.DerivedKeyRequired);

        if (string.IsNullOrWhiteSpace(address))
            return Result.Failure<Wallet>(WalletErrors.AddressRequired);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return Result.Success(new Wallet(
            Guid.CreateVersion7(), derivedKeyId, chain, address.Trim(), walletType, null, description, now));
    }

    public Result Disable(DateTimeOffset now)
    {
        if (Status == WalletStatus.Disabled)
            return Result.Success();

        ActiveAssignment?.Release(now);
        MerchantId = null;
        Status = WalletStatus.Disabled;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Releases the merchant's hold — e.g. when the merchant is closed. The wallet stays Active so it
    /// keeps receiving (and any late deposit remains attributable via the released assignment's
    /// history), but it is no longer the merchant's current address.
    /// </summary>
    public Result ReleaseAssignment(DateTimeOffset now)
    {
        var active = ActiveAssignment;
        if (active is null)
            return Result.Failure(WalletErrors.NotAssigned);

        active.Release(now);
        MerchantId = null;
        UpdatedAt = now;
        return Result.Success();
    }
}
