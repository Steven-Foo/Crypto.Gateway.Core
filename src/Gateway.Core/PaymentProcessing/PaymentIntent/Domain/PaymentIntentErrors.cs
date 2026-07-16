using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>Stable error codes — these cross the API boundary as RFC-9457 problem types (§7.1).</summary>
public static class PaymentIntentErrors
{
    public static readonly Error MerchantRequired =
        Error.Validation("payment_intent.merchant_required", "Merchant is required.");

    public static readonly Error ReferenceRequired =
        Error.Validation("payment_intent.reference_required", "Merchant transaction reference is required.");

    public static readonly Error AssetRequired =
        Error.Validation("payment_intent.asset_required", "Asset is required.");

    public static readonly Error WalletRequired =
        Error.Validation("payment_intent.wallet_required", "A deposit address is required.");

    public static readonly Error AmountNotPositive =
        Error.Validation("payment_intent.amount_not_positive", "Expected amount must be a positive base-unit integer.");

    public static readonly Error ExpiryInPast =
        Error.Validation("payment_intent.expiry_in_past", "Expiry must be in the future.");

    public static readonly Error GraceExpiryBeforeExpiry =
        Error.Validation("payment_intent.grace_expiry_before_expiry", "Grace expiry cannot be before the display expiry.");

    public static readonly Error DuplicateReference =
        Error.Conflict("payment_intent.duplicate_reference", "A payment intent already exists for this transaction reference.");

    public static readonly Error AddressUnavailable =
        Error.Conflict("payment_intent.address_unavailable", "Could not reserve a deposit address; retry shortly.");
}
