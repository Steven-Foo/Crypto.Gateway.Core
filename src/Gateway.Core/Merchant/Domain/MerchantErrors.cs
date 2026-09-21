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

    public static readonly Error DepositRangeInvalid =
        Error.Validation("merchant.deposit_range_invalid", "Minimum deposit must not exceed maximum deposit.");

    public static readonly Error AmountNegative =
        Error.Validation("merchant.amount_negative", "Base-unit amounts cannot be negative.");

    public static readonly Error AmountTooLarge =
        Error.Validation("merchant.amount_too_large", "Amount exceeds the 38-digit storage limit.");

    public static readonly Error FeeBpsInvalid =
        Error.Validation("merchant.fee_bps_invalid", "Fee basis points are out of range (must be 0-10000, where 10000 = 100%).");

    public static readonly Error SettlementAddressRequired =
        Error.Validation("merchant.settlement_address_required", "A settlement wallet address is required.");

    public static readonly Error WithdrawalCapBpsInvalid =
        Error.Validation("merchant.withdrawal_cap_bps_invalid", "Merchant-withdrawal percent cap must be 0-10000 basis points (0 = no cap).");

    public static readonly Error SettlementDelayInvalid =
        Error.Validation("merchant.settlement_delay_invalid", "Settlement period must be 0-30 days (T+N; 0 = T+0, immediate).");

    public static readonly Error WebhookRetryCountInvalid =
        Error.Validation("merchant.webhook_retry_count_invalid", "Webhook retry count must be between 0 and 20.");

    /// <summary>Deliberately indistinguishable from "unknown API key" — never reveal which failed.</summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("merchant.invalid_credentials", "Invalid API credentials.");

    public static readonly Error NotTransactable =
        Error.Unauthorized("merchant.not_transactable", "Merchant is not active.");

    /// <summary>
    /// The signature was authentic, but the call came from an address not on the merchant's IP allowlist (an empty
    /// allowlist permits none). Deliberately distinct from <see cref="InvalidCredentials"/>: it is reachable only
    /// with a genuine key AND signing secret, so it reveals nothing to a prober, and it tells the merchant what to fix.
    /// The message is the legacy gateway's, which merchants' integrations already know.
    /// </summary>
    public static readonly Error IpNotAllowed =
        Error.Failure("merchant.ip_not_allowed", "IP address not whitelisted.");

    public static readonly Error InvalidIpAddress =
        Error.Validation(
            "merchant.invalid_ip_address",
            "Allowed IPs must be single IPv4 or IPv6 addresses written in full: no CIDR ranges, ports or host names.");

    public static readonly Error NoValidIpsProvided =
        Error.Validation("merchant.no_valid_ips_provided", "No valid IP addresses were provided; existing allowed IPs are unchanged.");

    /// <summary>
    /// Address screening refused the proposed settlement wallet. Deliberately a hard refusal rather than a
    /// warning a staff member can wave through: this is the destination every one of that merchant's
    /// earnings is paid to, and a sanctions hit on it is a legal matter, not a risk appetite.
    /// </summary>
    public static readonly Error SettlementWalletBlocked =
        Error.Validation(
            "merchant.settlement_wallet_blocked",
            "Address screening refused this settlement wallet. It cannot be made the cash-out destination.");

    public static readonly Error SettlementWalletNotFound =
        Error.NotFound("merchant.settlement_wallet_not_found", "No such settlement wallet for this merchant.");

    /// <summary>Retiring the address a chain's cash-outs are paid to would leave the merchant unable to cash
    /// out at all. Activating a replacement retires it instead, so there is never a gap.</summary>
    public static readonly Error CannotRetireActiveSettlementWallet =
        Error.Conflict(
            "merchant.settlement_wallet_active",
            "This is the active cash-out destination. Activate a replacement instead, which retires it.");
}
