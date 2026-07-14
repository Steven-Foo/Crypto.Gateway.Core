using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// A posting could not be completed for a reason that must halt and alert rather than silently
/// proceed — e.g. a settlement that would drive custody negative, or an account that is not active.
/// These are money-integrity incidents: the worker should dead-letter the event for ops, never swallow
/// it. Lives in Domain so the Application layer can catch the one case it treats as an expected business
/// failure (a withdrawal reserve that exceeds the merchant's balance) and turn it into a Result.
/// </summary>
public sealed class LedgerPostingException(Error error)
    : Exception($"Ledger posting failed: {error.Code} — {error.Message}")
{
    public Error Error { get; } = error;
}
