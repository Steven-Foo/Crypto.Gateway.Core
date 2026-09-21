using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

/// <summary>Errors for the cold collection wallets Sweep concentrates deposits into.</summary>
public static class TreasuryColdWalletErrors
{
    public static readonly Error AddressRequired =
        Error.Validation("treasury.cold_wallet.address_required", "A cold collection wallet address is required.");

    /// <summary>
    /// The address is not well-formed for its chain. A format check only — it cannot tell a correct address
    /// from a different, equally valid one — but it catches the typo that would otherwise become the
    /// destination of every future sweep on the chain.
    /// </summary>
    public static readonly Error InvalidAddress =
        Error.Validation("treasury.cold_wallet.invalid_address", "The address is not valid for this chain.");

    /// <summary>The same address is already registered on this chain. Registering it twice tells an operator
    /// nothing new and leaves two rows the custody audit has to reason about.</summary>
    public static readonly Error AlreadyRegistered =
        Error.Conflict("treasury.cold_wallet.already_registered", "That address is already registered on this chain.");

    public static readonly Error NotFound =
        Error.NotFound("treasury.cold_wallet.not_found", "No such cold collection wallet.");

    public static readonly Error NotConfigured =
        Error.Conflict("treasury.cold_wallet.not_configured", "No cold collection wallet is registered for this chain.");

    /// <summary>Retiring the wallet a chain is currently sweeping into would leave sweeps with nowhere to go.
    /// Activate a replacement first — that retires this one as part of the same change.</summary>
    public static readonly Error CannotRetireActive =
        Error.Conflict(
            "treasury.cold_wallet.cannot_retire_active",
            "This wallet is the active destination. Activate a replacement instead, which retires it.");
}
