using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>Stable error codes — these cross the API boundary as RFC-9457 problem types (§7.1).</summary>
public static class MerchantErrors
{
    public static readonly Error CodeRequired =
        Error.Validation("merchant.code_required", "Merchant code is required.");

    public static readonly Error CodeInvalid =
        Error.Validation("merchant.code_invalid", "Merchant code must be 3-64 characters of A-Z, 0-9, '-' or '_'.");

    public static readonly Error NameRequired =
        Error.Validation("merchant.name_required", "Merchant name is required.");

    public static readonly Error CallbackUrlInvalid =
        Error.Validation("merchant.callback_url_invalid", "Callback URL must be an absolute http or https URL.");

    public static readonly Error CodeAlreadyExists =
        Error.Conflict("merchant.code_exists", "A merchant with that code already exists.");

    public static readonly Error NotFound =
        Error.NotFound("merchant.not_found", "Merchant not found.");

    public static readonly Error Closed =
        Error.Conflict("merchant.closed", "A closed merchant cannot be modified.");

    public static readonly Error CredentialNotFound =
        Error.NotFound("merchant.credential_not_found", "Credential not found for this merchant.");

    public static readonly Error CredentialAlreadyRevoked =
        Error.Conflict("merchant.credential_already_revoked", "Credential is already revoked.");

    public static readonly Error WithdrawalRangeInvalid =
        Error.Validation("merchant.withdrawal_range_invalid", "Minimum withdrawal must not exceed maximum withdrawal.");

    public static readonly Error AmountNegative =
        Error.Validation("merchant.amount_negative", "Base-unit amounts cannot be negative.");

    public static readonly Error AmountTooLarge =
        Error.Validation("merchant.amount_too_large", "Amount exceeds the 38-digit storage limit.");

    public static readonly Error WebhookRetryCountInvalid =
        Error.Validation("merchant.webhook_retry_count_invalid", "Webhook retry count must be between 0 and 20.");

    /// <summary>Deliberately indistinguishable from "unknown API key" — never reveal which failed.</summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("merchant.invalid_credentials", "Invalid API credentials.");

    public static readonly Error NotTransactable =
        Error.Unauthorized("merchant.not_transactable", "Merchant is not active.");
}
