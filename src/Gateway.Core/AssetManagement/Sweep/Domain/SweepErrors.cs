using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

public static class SweepErrors
{
    public static readonly Error WalletRequired =
        Error.Validation("sweep.wallet_required", "A sweep requires the source deposit wallet id.");

    public static readonly Error AddressRequired =
        Error.Validation("sweep.address_required", "A sweep requires both a source and destination address.");

    public static readonly Error AmountNotPositive =
        Error.Validation("sweep.amount_not_positive", "A sweep amount must be positive.");

    public static readonly Error InvalidStateTransition =
        Error.Conflict("sweep.invalid_state", "The sweep is not in a state that allows this transition.");

    public static readonly Error ActorRequired =
        Error.Validation("sweep.actor_required", "The staff member making the change must be recorded.");

    public static readonly Error ThresholdNegative =
        Error.Validation("sweep.threshold_negative", "The sweep threshold cannot be negative.");

    public static readonly Error ConfirmationsNotPositive =
        Error.Validation(
            "sweep.confirmations_not_positive",
            "At least one confirmation is required; zero would treat an unconfirmed transfer as final.");

    public static readonly Error ScanIntervalOutOfRange =
        Error.Validation(
            "sweep.scan_interval_out_of_range",
            $"The scan interval must be between {SweepSettings.MinScanIntervalMinutes} and "
            + $"{SweepSettings.MaxScanIntervalMinutes} minutes. Pause the chain instead of scheduling it further out.");

    public static readonly Error ChainPaused =
        Error.Conflict(
            "sweep.chain_paused",
            "Sweeping is paused for this chain. Enable it before requesting a scan.");

    public static readonly Error ChainNotConfigured =
        Error.NotFound(
            "sweep.chain_not_configured",
            "No sweep policy is configured for this chain, so it is never swept.");
}
