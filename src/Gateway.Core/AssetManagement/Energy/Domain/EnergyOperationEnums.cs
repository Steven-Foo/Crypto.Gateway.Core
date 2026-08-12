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
