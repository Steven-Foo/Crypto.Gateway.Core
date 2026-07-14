namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// The kind of account. The <see cref="NormalSide"/> of an account is a fixed consequence of its
/// type (assets/expenses are debit-normal; liabilities/revenue are credit-normal), so it is derived,
/// never supplied by a caller.
/// </summary>
public enum AccountType
{
    /// <summary>What the platform owes a merchant. Credit-normal. A merchant's spendable balance.</summary>
    MerchantLiability = 1,

    /// <summary>Asset the platform custodies on-chain, per asset. Debit-normal.</summary>
    TreasuryAsset = 2,

    /// <summary>Fees earned by the platform. Credit-normal.</summary>
    FeeRevenue = 3,

    /// <summary>Network (miner/validator) fees the platform bears. Debit-normal.</summary>
    NetworkFeeExpense = 4,

    /// <summary>Holds a withdrawal's funds in-flight, between reserve and settlement. Credit-normal.</summary>
    WithdrawalClearing = 5,
}

/// <summary>The side on which an account's balance naturally increases.</summary>
public enum NormalSide
{
    Debit = 1,
    Credit = 2,
}

/// <summary>Who an account belongs to. Treasury/System accounts have no owner id.</summary>
public enum OwnerType
{
    Merchant = 1,
    Treasury = 2,
    System = 3,
}

public enum AccountStatus
{
    Active = 1,
    Frozen = 2,
    Closed = 3,
}

/// <summary>The class of business event a journal records. Drives the idempotency key with ReferenceId.</summary>
public enum JournalReferenceType
{
    Deposit = 1,
    DepositReversal = 2,
    Withdrawal = 3,
    Sweep = 4,
    Settlement = 5,
    Adjustment = 6,

    /// <summary>Funds locked when a withdrawal is requested (the atomic balance check).</summary>
    WithdrawalReserve = 7,

    /// <summary>Funds actually leave custody once the withdrawal confirms on-chain.</summary>
    WithdrawalSettle = 8,

    /// <summary>Reserved funds returned to the merchant when a withdrawal is rejected or fails.</summary>
    WithdrawalRelease = 9,
}

/// <summary>Which column a posting line lands in. A line is a debit XOR a credit — never both, never neither.</summary>
public enum EntryDirection
{
    Debit = 1,
    Credit = 2,
}
