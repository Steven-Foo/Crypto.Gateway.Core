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

/// <summary>
/// Why money arrived: a customer paying a merchant, or the merchant funding its own balance. Both are real
/// on-chain deposits to a platform address and share the identical detection → confirmation → ledger
/// pipeline and ledger impact (<c>Dr TreasuryAsset / Cr MerchantLiability [+ Cr FeeRevenue]</c>) — so this
/// is one aggregate with a discriminator, never a parallel flow (the <c>WithdrawalKind</c> precedent).
///
/// <para>They differ only in <em>pricing</em> and reporting: a top-up is quoted from the merchant's separate
/// top-up fee (default zero) rather than the customer deposit fee, and is deducted rather than grossed up.
/// The kind is declared on the invoice when the merchant creates it — the scanner cannot infer it, because
/// on-chain a top-up and a customer payment to the same address are indistinguishable.</para>
/// </summary>
public enum DepositKind
{
    /// <summary>A customer paying the merchant. The default: every deposit predating top-up is this, and a
    /// deposit that matches no invoice is treated as this.</summary>
    Customer = 0,

    /// <summary>The merchant funding its own balance by sending crypto to its deposit address.</summary>
    MerchantTopUp = 1,
}
