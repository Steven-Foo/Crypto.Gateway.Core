using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

public static class TreasuryReloadErrors
{
    public static readonly Error AmountNotPositive =
        Error.Validation("treasury.reload.amount_not_positive", "The reload amount must be greater than zero.");

    public static readonly Error AddressRequired =
        Error.Validation("treasury.reload.address_required", "A source (cold) and target (hot) address are required.");

    public static readonly Error TargetWalletRequired =
        Error.Validation("treasury.reload.target_required", "A target hot wallet is required.");

    public static readonly Error UnsignedPayloadRequired =
        Error.Validation("treasury.reload.unsigned_required", "The unsigned transaction payload is required.");

    public static readonly Error SignedPayloadRequired =
        Error.Validation("treasury.reload.signed_required", "The signed transaction blob is required.");

    public static readonly Error InvalidStateTransition =
        Error.Conflict("treasury.reload.invalid_state", "The reload is not in a state that allows this operation.");

    public static readonly Error NotFound =
        Error.NotFound("treasury.reload.not_found", "Reload not found.");

    public static readonly Error ColdWalletNotConfigured =
        Error.Conflict("treasury.cold_wallet.not_configured", "No cold treasury wallet is registered for this chain.");
}
