namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

/// <summary>
/// The sweep lifecycle: moving accumulated funds from a deposit address into the hot wallet. Unlike a
/// withdrawal there is <b>no ledger reserve</b> — a sweep relocates funds between addresses the platform
/// already controls, so total custody (the ledger's <c>TreasuryAsset</c>) is unchanged and no ledger entry
/// is posted for the principal. A failure is only reachable <em>before</em> broadcast (nothing left the
/// chain); once broadcast, only <see cref="Confirmed"/> or an ops incident.
/// </summary>
public enum SweepStatus
{
    /// <summary>Created from a scan — a deposit address's balance cleared the threshold. Not yet built/signed.</summary>
    Pending = 0,

    /// <summary>An unsigned transaction has been built, signed, and the signed blob persisted.</summary>
    Signing = 1,

    /// <summary>The signed transaction has been broadcast to the chain.</summary>
    Broadcast = 2,

    /// <summary>Confirmed on-chain under the policy depth. Custody is now concentrated in the hot wallet.</summary>
    Confirmed = 3,

    /// <summary>Failed before broadcast — nothing left the chain, so the deposit address is untouched.</summary>
    Failed = 4,
}
