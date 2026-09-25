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

    /// <summary>The merchant itself is Closed — a full portal shutout, distinct from one account being
    /// individually <see cref="AccountDisabled"/>. Applies to every account under the merchant regardless of
    /// role, and is checked both at login and on every subsequent request (see
    /// <c>MerchantAuthService.ValidateAsync</c>), so an already-open session is cut off immediately once the
    /// merchant closes, not merely refused at its next login.</summary>
    public static readonly Error MerchantClosed =
        Error.Unauthorized("merchant_user.merchant_closed", "This merchant account has been closed.");

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

    public static readonly Error CannotDisablePrimaryAccount =
        Error.Conflict("merchant_user.cannot_disable_primary_account", "The merchant's primary account cannot be disabled.");

    /// <summary>Platform staff may reset ONLY the merchant's primary account — a teammate account is the
    /// merchant's own internal business, reset by the merchant's own admin inside the portal, never by staff
    /// (§ platform-side password reset is primary-only by design). Forbidden, not NotFound: the target account
    /// genuinely exists, staff simply may not act on it this way.</summary>
    public static readonly Error OnlyPrimaryResettableByStaff =
        Error.Forbidden(
            "merchant_user.only_primary_resettable_by_staff",
            "Platform staff may only reset the merchant's primary account. Other accounts are managed by the merchant inside its own portal.");

    /// <summary>The requested role belongs to a different merchant (or does not exist). Surfaced identically
    /// either way so the response never confirms that another tenant's role id is real.</summary>
    public static readonly Error RoleNotInTenant =
        Error.Validation("merchant_user.role_not_in_tenant", "Role not found.");

    public static readonly Error CurrentPasswordIncorrect =
        Error.Unauthorized("merchant_user.current_password_incorrect", "The current password is incorrect.");

    public static readonly Error PasswordTooShort =
        Error.Validation("merchant_user.password_too_short", "The new password must be at least 12 characters.");

    /// <summary>Mirrors <c>Platform.Identity.StaffUserErrors.CannotChangeOwnTwoFactorRequirement</c> — flipping
    /// your OWN switch off would let you escape 2FA with nobody else's sign-off. Another portal admin must do
    /// it, same as an account cannot disable itself.</summary>
    public static readonly Error CannotChangeOwnTwoFactorRequirement =
        Error.Conflict("merchant_user.cannot_change_own_two_factor_requirement", "You cannot change your own 2FA requirement.");
}
