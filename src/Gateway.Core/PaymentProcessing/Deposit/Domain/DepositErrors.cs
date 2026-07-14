using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;

public static class DepositErrors
{
    public static readonly Error AddressRequired =
        Error.Validation("deposit.address_required", "A deposit must reference an address.");

    public static readonly Error TransactionHashRequired =
        Error.Validation("deposit.tx_hash_required", "A deposit must reference a transaction hash.");

    public static readonly Error AmountNotPositive =
        Error.Validation("deposit.amount_not_positive", "A deposit amount must be greater than zero.");

    public static readonly Error OwnerRequired =
        Error.Validation("deposit.owner_required", "A deposit must reference the owning wallet, merchant, and asset.");

    public static readonly Error NotFound =
        Error.NotFound("deposit.not_found", "Deposit not found.");
}
