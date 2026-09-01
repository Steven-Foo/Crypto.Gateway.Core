using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

public static class MerchantUserErrors
{
    public static readonly Error MerchantRequired =
        Error.Validation("merchant_user.merchant_required", "A merchant is required.");

    public static readonly Error UsernameRequired =
        Error.Validation("merchant_user.username_required", "Username is required.");

    public static readonly Error PasswordHashRequired =
        Error.Validation("merchant_user.password_hash_required", "A password hash is required.");

    public static readonly Error AccountDisabled =
        Error.Unauthorized("merchant_user.account_disabled", "This account has been disabled.");

    public static readonly Error InvalidCredentials =
        Error.Unauthorized("merchant_user.invalid_credentials", "Invalid username or password.");

    public static readonly Error SessionExpiredOrRevoked =
        Error.Unauthorized("merchant_user.session_expired_or_revoked", "The session is expired or has been revoked.");

    public static readonly Error NotFound =
        Error.NotFound("merchant_user.not_found", "Account not found.");

    public static readonly Error UsernameAlreadyExists =
        Error.Conflict("merchant_user.username_already_exists", "An account with this username already exists.");

    public static readonly Error CannotDisableSelf =
        Error.Conflict("merchant_user.cannot_disable_self", "You cannot disable your own account.");

    public static readonly Error CannotDisableLastActiveAccount =
        Error.Conflict("merchant_user.cannot_disable_last_active_account", "At least one active account must remain.");

    /// <summary>The requested role belongs to a different merchant (or does not exist). Surfaced identically
    /// either way so the response never confirms that another tenant's role id is real.</summary>
    public static readonly Error RoleNotInTenant =
        Error.Validation("merchant_user.role_not_in_tenant", "Role not found.");

    public static readonly Error CurrentPasswordIncorrect =
        Error.Unauthorized("merchant_user.current_password_incorrect", "The current password is incorrect.");

    public static readonly Error PasswordTooShort =
        Error.Validation("merchant_user.password_too_short", "The new password must be at least 12 characters.");
}
