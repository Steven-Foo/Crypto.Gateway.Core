using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;

public static class WalletErrors
{
    public static readonly Error AddressRequired =
        Error.Validation("wallet.address_required", "A wallet address is required.");

    public static readonly Error DerivedKeyRequired =
        Error.Validation("wallet.derived_key_required", "A derived-key reference is required.");

    public static readonly Error MerchantRequiredForDeposit =
        Error.Validation("wallet.merchant_required", "A deposit wallet must be assigned to a merchant.");

    public static readonly Error MerchantNotAllowedForPlatform =
        Error.Validation("wallet.merchant_not_allowed", "A platform wallet cannot be assigned to a merchant.");

    public static readonly Error MerchantNotFound =
        Error.NotFound("wallet.merchant_not_found", "The merchant does not exist.");

    public static readonly Error MerchantCannotTransact =
        Error.Conflict("wallet.merchant_cannot_transact", "The merchant is not active and cannot be assigned a wallet.");

    public static readonly Error NotFound =
        Error.NotFound("wallet.not_found", "Wallet not found.");

    public static readonly Error Disabled =
        Error.Conflict("wallet.disabled", "The wallet is disabled.");

    public static readonly Error AlreadyAssigned =
        Error.Conflict("wallet.already_assigned", "The wallet already has an active assignment.");

    public static readonly Error NotAssigned =
        Error.Conflict("wallet.not_assigned", "The wallet has no active assignment.");

    public static readonly Error DerivedChainMismatch =
        Error.Validation("wallet.derived_chain_mismatch", "The derived key's chain does not match the requested chain.");
}
