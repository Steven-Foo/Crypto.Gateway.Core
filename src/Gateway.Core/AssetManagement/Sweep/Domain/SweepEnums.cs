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

/// <summary>
/// Which cold collection wallet a sweep was routed to, decided by screening the deposit address before the
/// sweep was created.
///
/// <para>Recorded on the sweep itself rather than inferred later from the destination address: the
/// destination of a given kind can be replaced, and a sweep has to stay explainable against the decision
/// that was actually made at the time — the same reason a payout snapshots its screening verdict.</para>
///
/// <para>Deliberately Sweep's own enum rather than a reference to Treasury's <c>ColdWalletKind</c>: the
/// Domain layer depends on nothing outside its module (§4.4), and the two are mapped in the Application
/// layer where the destination is resolved.</para>
/// </summary>
public enum SweepDestinationKind
{
    /// <summary>The deposit address screened clean — or screening is switched off, which is the platform's
    /// pre-segregation behaviour and keeps every sweep going where it always did.</summary>
    Safe = 0,

    /// <summary>The deposit address was flagged, so the balance goes to the quarantine wallet instead of
    /// being mixed into clean treasury.</summary>
    Danger = 1,
}
