using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// A single-asset bucket in the double-entry system. An account is uniquely identified by
/// <c>(OwnerType, OwnerId, AssetId, AccountType)</c>; the DB enforces that uniqueness so the
/// get-or-create race is settled by a constraint, not by application logic.
///
/// The Ledger treats <see cref="AssetId"/> as an opaque unit of account — it never knows which chain
/// the asset lives on (§4.6). Balancing is per asset.
/// </summary>
public sealed class Account : Entity<Guid>
{
    private Account(
        Guid id,
        AccountType accountType,
        OwnerType ownerType,
        Guid? ownerId,
        Guid assetId,
        NormalSide normalSide,
        DateTimeOffset createdAt) : base(id)
    {
        AccountType = accountType;
        OwnerType = ownerType;
        OwnerId = ownerId;
        AssetId = assetId;
        NormalSide = normalSide;
        Status = AccountStatus.Active;
        CreatedAt = createdAt;
    }

    private Account() : base(Guid.Empty)
    {
    }

    public AccountType AccountType { get; private set; }
    public OwnerType OwnerType { get; private set; }

    /// <summary>The merchant this account belongs to; null for treasury/system accounts.</summary>
    public Guid? OwnerId { get; private set; }

    public Guid AssetId { get; private set; }
    public NormalSide NormalSide { get; private set; }
    public AccountStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => Status == AccountStatus.Active;

    /// <summary>
    /// Opens an account. The <see cref="NormalSide"/> is derived from <paramref name="accountType"/>,
    /// never supplied — so a liability can never be mis-declared debit-normal.
    /// </summary>
    public static Result<Account> Open(
        AccountType accountType,
        OwnerType ownerType,
        Guid? ownerId,
        Guid assetId,
        DateTimeOffset now)
    {
        if (assetId == Guid.Empty)
            return Result.Failure<Account>(LedgerErrors.AssetRequired);

        if (ownerType == OwnerType.Merchant && (ownerId is null || ownerId == Guid.Empty))
            return Result.Failure<Account>(LedgerErrors.MerchantAccountNeedsOwner);

        if (ownerType != OwnerType.Merchant && ownerId is not null)
            return Result.Failure<Account>(LedgerErrors.PlatformAccountHasNoOwner);

        return Result.Success(new Account(
            Guid.CreateVersion7(), accountType, ownerType, ownerId, assetId, NormalSideOf(accountType), now));
    }

    /// <summary>Rehydrates an account whose identity is already known (e.g. a well-known system account).</summary>
    public static Account FromTrusted(
        Guid id, AccountType accountType, OwnerType ownerType, Guid? ownerId, Guid assetId, DateTimeOffset createdAt) =>
        new(id, accountType, ownerType, ownerId, assetId, NormalSideOf(accountType), createdAt);

    /// <summary>Accounting truth: assets and expenses grow on the debit side; liabilities and revenue on the credit side.</summary>
    public static NormalSide NormalSideOf(AccountType type) => type switch
    {
        AccountType.TreasuryAsset => NormalSide.Debit,
        AccountType.NetworkFeeExpense => NormalSide.Debit,
        AccountType.MerchantLiability => NormalSide.Credit,
        AccountType.FeeRevenue => NormalSide.Credit,
        AccountType.WithdrawalClearing => NormalSide.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type has no defined normal side."),
    };
}
