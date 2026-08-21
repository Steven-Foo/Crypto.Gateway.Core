namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// The canonical catalog of Ops permission codes — the vocabulary every <c>.RequirePermission(...)</c> call
/// and the Roles UI both read from, so there is exactly one place that can drift. Owned by the host, not
/// Identity (§4.5): these codes name capabilities across Merchant/Withdrawal/PaymentIntent/Notification, and
/// Identity must not know those modules exist — it only ever stores/snapshots opaque strings.
///
/// Naming: <c>ops.&lt;module&gt;.&lt;verb&gt;</c>. <c>.view</c> covers a module's read/search screens; other
/// verbs are the specific mutating actions on that screen. A role needs no code at all to have a module fully
/// hidden from it — the frontend derives "show this nav item" from "does the caller hold any code with this
/// module's prefix" (see <c>GET /api/v1/ops/auth/me</c>).
/// </summary>
public static class OpsPermissions
{
    public static class Merchants
    {
        public const string View = "ops.merchants.view";
        public const string Manage = "ops.merchants.manage";
        public const string RotateKey = "ops.merchants.rotate-key";
    }

    public static class Fees
    {
        public const string View = "ops.fees.view";
        public const string Manage = "ops.fees.manage";
    }

    public static class Deposits
    {
        public const string View = "ops.deposits.view";
        public const string Manage = "ops.deposits.manage"; // e.g. manually failing a stuck payment intent
    }

    public static class Withdrawals
    {
        public const string View = "ops.withdrawals.view";
        public const string Approve = "ops.withdrawals.approve"; // approve or reject a PendingApproval withdrawal
        public const string Manage = "ops.withdrawals.manage"; // release/cancel a funding hold
    }

    public static class Transactions
    {
        public const string View = "ops.transactions.view"; // the ledger-wide journal search
    }

    public static class Callbacks
    {
        public const string Manage = "ops.callbacks.manage"; // resend an abandoned webhook
    }

    public static class Roles
    {
        public const string View = "ops.roles.view";
        public const string Manage = "ops.roles.manage";
    }

    public static class Accounts
    {
        public const string View = "ops.accounts.view";
        public const string Manage = "ops.accounts.manage"; // create, status, role reassignment, password reset
    }

    public static class Audit
    {
        public const string View = "ops.audit.view";
    }

    public static class Wallets
    {
        public const string View = "ops.wallets.view";
        public const string Manage = "ops.wallets.manage"; // suspend/resume a deposit address
    }

    public static class Treasury
    {
        // Every treasury route is gated by this one code, including the hot-pool read — even listing the
        // pool is considered sensitive custody-adjacent info, per OpsTreasuryEndpoints' own doc comment.
        public const string Manage = "ops.treasury.manage";
    }

    public static class Sweep
    {
        public const string View = "ops.sweep.view"; // read the sweep state machine (deposit → cold treasury)
    }

    public static class Energy
    {
        // Read the TRON energy state: stake/delegate/top-up operations + per-wallet resource-health snapshots.
        public const string View = "ops.energy.view";
    }

    /// <summary>Every known code, flattened — what backs <c>GET /api/v1/ops/permissions</c> (the catalog a
    /// Roles-editor UI assigns from). Reflection-free on purpose: an explicit list is easier to audit than a
    /// reflective scan, and this file is the one place that has to stay in sync with the endpoints anyway.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Merchants.View, Merchants.Manage, Merchants.RotateKey,
        Fees.View, Fees.Manage,
        Deposits.View, Deposits.Manage,
        Withdrawals.View, Withdrawals.Approve, Withdrawals.Manage,
        Transactions.View,
        Callbacks.Manage,
        Roles.View, Roles.Manage,
        Accounts.View, Accounts.Manage,
        Audit.View,
        Wallets.View, Wallets.Manage,
        Treasury.Manage,
        Sweep.View,
        Energy.View,
    ];
}
