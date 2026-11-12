namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;

/// <summary>What an <see cref="EnergyOperation"/> does on-chain. Both share one sign→broadcast→confirm
/// lifecycle, so they are one aggregate discriminated by kind (§12 DRY), not two near-duplicate tables.</summary>
public enum EnergyOperationKind
{
    /// <summary>Freeze platform TRX to acquire energy on the staking wallet (TRON FreezeBalanceV2). Recoverable.</summary>
    Stake = 1,

    /// <summary>Delegate staked energy from the staking wallet to a target address (TRON DelegateResource),
    /// e.g. a deposit address about to be swept. Reclaimable.</summary>
    Delegate = 2,

    /// <summary>Send a small amount of native TRX from the staking (gas hub) wallet to a target address so it can
    /// pay <em>bandwidth</em> (which energy delegation does not cover). Unlike Stake/Delegate this is a real
    /// transfer, but between platform-controlled addresses — custody-internal, so still no ledger entry.</summary>
    TopUp = 3,

    /// <summary>Take back energy lent to an address that no longer needs it (TRON UnDelegateResource). The staked
    /// TRX returns to the staking wallet's lendable balance at once. Created by the take-back worker.</summary>
    Undelegate = 4,

    /// <summary>Start unlocking staked TRX (TRON UnfreezeBalanceV2). Staff-initiated. The TRX can be withdrawn
    /// only after the network's waiting period (14 days on mainnet).</summary>
    Unstake = 5,

    /// <summary>Move every finished unstake back into the staking wallet's spendable balance (TRON
    /// WithdrawExpireUnfreeze). Staff-initiated. The amount is decided by the chain, so it is recorded as 0.</summary>
    WithdrawUnstaked = 6,
}

/// <summary>
/// The energy-operation lifecycle, mirroring the withdrawal/sweep money-out shape. Neither stake nor delegate
/// spends value (frozen/delegated TRX stays the platform's, recoverable), so an operation posts NO ledger
/// entry; the signed blob is persisted before broadcast so a crash-retry re-broadcasts the same tx.
/// </summary>
public enum EnergyOperationStatus
{
    Pending = 0,
    Signing = 1,
    Broadcast = 2,
    Confirmed = 3,
    Failed = 4,
}
