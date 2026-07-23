using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;

/// <summary>
/// A read-only signal for UX composition only — never the money path. Lets a caller (the hosted pay page)
/// show "payment seen, confirming on-chain" the moment the scanner detects a transfer, well before it
/// reaches the credit threshold. The Ledger credit and merchant webhook still wait for the full
/// <c>DepositConfirmed</c> event; this never influences that decision (§4.5, §9).
/// </summary>
public interface IDepositLookup
{
    /// <summary>True if an unconfirmed (<c>Detected</c>) deposit currently sits at this address.</summary>
    Task<bool> HasDetectedDepositAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}
