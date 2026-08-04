using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

public static class TreasuryErrors
{
    public static readonly Error HotWalletNotConfigured =
        Error.NotFound(
            "treasury.hot_wallet_not_configured",
            "No hot withdrawal wallet is registered for this chain.");

    /// <summary>
    /// More than one active <c>HotWithdrawal</c> wallet exists for the chain. Multi-wallet selection is out
    /// of scope for this cut, so we refuse rather than silently pick one — a withdrawal must never be signed
    /// from an arbitrary wallet.
    /// </summary>
    public static readonly Error HotWalletAmbiguous =
        Error.Conflict(
            "treasury.hot_wallet_ambiguous",
            "More than one hot withdrawal wallet is registered for this chain.");

    public static readonly Error SigningKeyMissing =
        Error.NotFound(
            "treasury.signing_key_missing",
            "The hot wallet has no registered signing key for this chain.");
}
