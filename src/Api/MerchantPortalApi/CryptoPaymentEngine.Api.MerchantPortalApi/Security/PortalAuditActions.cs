namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// The stable action codes this host writes into the audit log. Owned by the host, not by the Audit module —
/// Audit deliberately does not know what any code means (§4.5), it only stores and filters them.
///
/// <para>Codes are <c>&lt;entity&gt;.&lt;past-tense verb&gt;</c>, matching the Back Office convention
/// (<c>withdrawal.approved</c>, <c>merchant.fee_updated</c>) so one query vocabulary spans both hosts.
/// Treat them as append-only: renaming one orphans the history already written under the old name.</para>
/// </summary>
public static class PortalAuditActions
{
    public const string EntityRole = "MerchantRole";
    public const string EntityAccount = "MerchantUser";
    public const string EntityCredential = "MerchantApiCredential";
    public const string EntityMerchant = "Merchant";
    public const string EntityWithdrawal = "Withdrawal";

    public const string RoleCreated = "portal.role.created";
    public const string RoleUpdated = "portal.role.updated";
    public const string RolePermissionsChanged = "portal.role.permissions_changed";
    public const string RoleDeleted = "portal.role.deleted";

    public const string AccountCreated = "portal.account.created";
    public const string AccountStatusChanged = "portal.account.status_changed";
    public const string AccountRoleChanged = "portal.account.role_changed";
    public const string AccountPasswordReset = "portal.account.password_reset";
    public const string OwnPasswordChanged = "portal.account.own_password_changed";

    public const string ApiCredentialRotated = "portal.api_credential.rotated";
    public const string AllowedIpsUpdated = "portal.allowed_ips.updated";

    public const string PayoutApproved = "portal.payout.approved";
    public const string PayoutRejected = "portal.payout.rejected";
}
