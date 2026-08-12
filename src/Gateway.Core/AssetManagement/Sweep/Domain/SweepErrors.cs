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
}
