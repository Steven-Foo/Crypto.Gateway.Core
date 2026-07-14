namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;

/// <summary>
/// The lifecycle of a detected on-chain deposit. Credit happens only at <see cref="Confirmed"/> —
/// never on first sight — so a reorg before confirmation costs nothing.
/// </summary>
public enum DepositStatus
{
    /// <summary>Seen on-chain, not yet at the required confirmation depth. Nothing credited.</summary>
    Detected = 1,

    /// <summary>Reached the credit threshold; <c>DepositConfirmed</c> published → the Ledger credits.</summary>
    Confirmed = 2,

    /// <summary>The block that carried it was reorged out. If it had been confirmed, <c>DepositOrphaned</c> reverses it.</summary>
    Orphaned = 3,

    /// <summary>Below the asset's minimum deposit (dust). Recorded for audit, never credited.</summary>
    Ignored = 4,
}

/// <summary>How a deposit becomes creditable, per chain policy.</summary>
public enum CreditStrategy
{
    /// <summary>Credit once it is buried under N confirmations (Tron, Ethereum).</summary>
    Confirmations = 1,

    /// <summary>Credit once the chain reports it final/irreversible (Solana 'finalized' commitment).</summary>
    Finalized = 2,
}
