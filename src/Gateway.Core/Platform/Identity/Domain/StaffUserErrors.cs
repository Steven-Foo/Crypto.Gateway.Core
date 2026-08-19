using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

public static class StaffUserErrors
{
    public static readonly Error UsernameRequired =
        Error.Validation("staff_user.username_required", "Username is required.");

    public static readonly Error PasswordHashRequired =
        Error.Validation("staff_user.password_hash_required", "A password hash is required.");

    public static readonly Error RoleRequired =
        Error.Validation("staff_user.role_required", "A role is required.");

    public static readonly Error AccountDisabled =
        Error.Unauthorized("staff_user.account_disabled", "This account has been disabled.");

    public static readonly Error InvalidCredentials =
        Error.Unauthorized("staff_user.invalid_credentials", "Invalid username or password.");

    public static readonly Error UsernameAlreadyExists =
        Error.Conflict("staff_user.username_already_exists", "A staff user with this username already exists.");

    public static readonly Error SessionExpiredOrRevoked =
        Error.Unauthorized("staff_user.session_expired_or_revoked", "The session is expired or has been revoked.");

    public static readonly Error NotFound =
        Error.NotFound("staff_user.not_found", "Staff account not found.");

    public static readonly Error CannotDisableSelf =
        Error.Conflict("staff_user.cannot_disable_self", "You cannot disable your own account.");

    public static readonly Error CannotDisableLastActiveAccount =
        Error.Conflict("staff_user.cannot_disable_last_active_account", "At least one active staff account must remain.");
}
