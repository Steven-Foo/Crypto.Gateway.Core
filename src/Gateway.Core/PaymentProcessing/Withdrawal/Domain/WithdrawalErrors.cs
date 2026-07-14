using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

public static class WithdrawalErrors
{
    public static readonly Error DestinationRequired =
        Error.Validation("withdrawal.destination_required", "A destination address is required.");

    public static readonly Error AmountNotPositive =
        Error.Validation("withdrawal.amount_not_positive", "The withdrawal amount must be greater than zero.");

    public static readonly Error OwnerRequired =
        Error.Validation("withdrawal.owner_required", "A withdrawal must reference a merchant and an asset.");

    public static readonly Error IdempotencyKeyRequired =
        Error.Validation("withdrawal.idempotency_key_required", "An idempotency key is required.");

    public static readonly Error BelowMinimum =
        Error.Validation("withdrawal.below_minimum", "The amount is below the minimum withdrawal for this asset.");

    public static readonly Error AboveMaximum =
        Error.Validation("withdrawal.above_maximum", "The amount exceeds the maximum withdrawal for this asset.");

    public static readonly Error MerchantCannotTransact =
        Error.Conflict("withdrawal.merchant_cannot_transact", "The merchant is not active and cannot withdraw.");

    public static readonly Error InsufficientBalance =
        Error.Conflict("withdrawal.insufficient_balance", "The merchant's balance is insufficient for this withdrawal.");

    public static readonly Error DuplicateRequest =
        Error.Conflict("withdrawal.duplicate_request", "A withdrawal with this idempotency key already exists.");

    public static readonly Error InvalidStateTransition =
        Error.Conflict("withdrawal.invalid_state", "The withdrawal is not in a state that allows this operation.");

    public static readonly Error NotFound =
        Error.NotFound("withdrawal.not_found", "Withdrawal not found.");
}
