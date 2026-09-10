namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// The canonical catalog of merchant-portal permission codes — the vocabulary every
/// <c>.RequirePortalPermission(...)</c> call and the portal's own Roles screen read from, so there is exactly
/// one place that can drift. Owned by the host, not MerchantIdentity (§4.5): these codes name capabilities
/// across Merchant/Withdrawal/Ledger, and the identity module must not know those modules exist — it only
/// stores and snapshots opaque strings.
///
/// <para><b>Why the host validates against this list:</b> a merchant admin composes its own roles, so without
/// a closed vocabulary a tenant could store any string it liked — including a platform <c>ops.*</c> code. Those
/// are enforced by a different catalog on a different host, so it would never actually grant Ops access, but
/// storing them would be misleading at best. Codes are validated at the edge before they reach the module.</para>
///
/// Naming: <c>portal.&lt;module&gt;.&lt;verb&gt;</c>.
/// </summary>
public static class PortalPermissions
{
    public static class Overview
    {
        public const string View = "portal.overview.view"; // profile, funds, fees, deposit addresses
    }

    public static class Transactions
    {
        public const string View = "portal.transactions.view"; // payin / payout / cash-out history
    }

    public static class Payouts
    {
        /// <summary>Submit an end-user payout to a merchant-supplied destination address. The most sensitive
        /// portal permission: it moves money to an address chosen in the request, so it is deliberately its own
        /// code, off by default, and granted only to roles a merchant admin explicitly trusts. Amounts above the
        /// per-merchant approval threshold still require platform staff approval regardless.</summary>
        public const string Create = "portal.payouts.create";

        /// <summary>Sign off a payout another portal user submitted (the merchant-side half of the two-party
        /// rule). Deliberately a SEPARATE code from Create: a user who may only submit cannot also approve, so
        /// the separation of duties comes from the permission split, not from a different-user check.</summary>
        public const string Approve = "portal.payouts.approve";
    }

    public static class Activity
    {
        /// <summary>
        /// Read this merchant's own administrative activity log — who changed roles, created or disabled
        /// accounts, rotated the API credential, edited the IP allowlist, or approved a payout.
        ///
        /// <para>Its own code because the log is itself sensitive: it names every account and shows when
        /// security controls were changed, which is reconnaissance for anyone who should not have it. It reads
        /// ONLY this tenant's entries — platform-staff actions and other merchants' are excluded structurally,
        /// not by filter (see <c>AuditScope</c>).</para>
        /// </summary>
        public const string View = "portal.activity.view";
    }

    public static class CashOut
    {
        /// <summary>Initiate a merchant earnings cash-out. Lower risk than a payout: the destination is the
        /// settlement wallet whitelisted by platform staff, never supplied by the request (§10).</summary>
        public const string Create = "portal.cashout.create";
    }

    public static class TopUp
    {
        /// <summary>
        /// Create a top-up invoice — the merchant funding its own balance on-chain.
        ///
        /// <para>Its own code, off by default, because a top-up is credited <b>immediately</b>: it is exempt
        /// from the merchant's T+N settlement hold, since a merchant's own float is not customer money awaiting
        /// chargeback risk. That exemption is the point of the feature, but it also means funds routed through
        /// a top-up invoice are withdrawable at once — so a merchant admin decides who may create one, rather
        /// than every portal user inheriting it. It cannot inflate a balance (real crypto must actually
        /// arrive); the control is over timing, not value.</para>
        /// </summary>
        public const string Create = "portal.topup.create";
    }

    public static class ApiCredentials
    {
        public const string View = "portal.api.view";       // read key metadata + allowed IPs (never a secret)
        public const string Manage = "portal.api.manage";   // rotate the API credential, update allowed IPs
    }

    public static class Accounts
    {
        public const string View = "portal.accounts.view";
        public const string Manage = "portal.accounts.manage"; // create, enable/disable, role assignment, password reset
    }

    public static class Roles
    {
        public const string View = "portal.roles.view";
        public const string Manage = "portal.roles.manage";
    }

    /// <summary>Every known code, flattened — what backs <c>GET /api/v1/portal/permissions</c> (the catalog the
    /// portal's Roles editor assigns from) and what an incoming role's codes are validated against. Explicit,
    /// not reflective, so it is easy to audit.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Overview.View,
        Transactions.View,
        Payouts.Create, Payouts.Approve,
        CashOut.Create,
        TopUp.Create,
        ApiCredentials.View, ApiCredentials.Manage,
        Accounts.View, Accounts.Manage,
        Roles.View, Roles.Manage,
        Activity.View,
    ];

    /// <summary>True if every supplied code is either the wildcard or a known portal code. The wildcard is
    /// accepted so a merchant can define its own full-access role.</summary>
    public static bool AreAllKnown(IEnumerable<string> codes, out string? firstUnknown)
    {
        foreach (var code in codes)
        {
            var trimmed = code.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed == PortalWildcard || All.Contains(trimmed, StringComparer.Ordinal))
                continue;

            firstUnknown = trimmed;
            return false;
        }

        firstUnknown = null;
        return true;
    }

    /// <summary>Mirrors <c>MerchantRole.WildcardPermission</c> — kept as its own constant so the host does not
    /// need to reference the identity module's Domain just to name it.</summary>
    public const string PortalWildcard = "*";
}
