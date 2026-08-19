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

    public static readonly Error MerchantTransactionIdRequired =
        Error.Validation("withdrawal.merchant_transaction_id_required", "A merchant transaction id is required.");

    public static readonly Error BelowMinimum =
        Error.Validation("withdrawal.below_minimum", "The amount is below the minimum withdrawal for this asset.");

    public static readonly Error AboveMaximum =
        Error.Validation("withdrawal.above_maximum", "The amount exceeds the maximum withdrawal for this asset.");

    public static readonly Error SettlementWalletNotRegistered =
        Error.Conflict("withdrawal.settlement_wallet_not_registered", "No settlement wallet is registered for this merchant and chain.");

    public static readonly Error ExceedsMerchantWithdrawalLimit =
        Error.Validation("withdrawal.exceeds_merchant_withdrawal_limit", "The amount exceeds your merchant withdrawal (cash-out) limit.");

    public static readonly Error ExceedsSettledBalance =
        Error.Validation("withdrawal.exceeds_settled_balance", "The amount exceeds the settled (withdrawable) balance; some funds are still within the settlement period (T+N).");

    public static readonly Error MerchantCannotTransact =
        Error.Conflict("withdrawal.merchant_cannot_transact", "The merchant is not active and cannot withdraw.");

    public static readonly Error InsufficientBalance =
        Error.Conflict("withdrawal.insufficient_balance", "The merchant's balance is insufficient for this withdrawal.");

    public static readonly Error DuplicateReference =
        Error.Conflict("withdrawal.duplicate_reference", "Duplicate transactionId.");

    public static readonly Error InvalidStateTransition =
        Error.Conflict("withdrawal.invalid_state", "The withdrawal is not in a state that allows this operation.");

    public static readonly Error NotFound =
        Error.NotFound("withdrawal.not_found", "Withdrawal not found.");
}
