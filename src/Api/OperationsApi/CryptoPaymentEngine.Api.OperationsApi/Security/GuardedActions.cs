using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>One guardable action, as the settings screen renders it.</summary>
/// <param name="Code">The opaque string stored in the policy and matched by the filter.</param>
/// <param name="Group">Which screen it belongs to, so the UI can group the checkboxes.</param>
/// <param name="Label">What to call it to a human. Never the code itself.</param>
/// <param name="Recommended">Part of the recommended security baseline — what a fresh environment guards
/// before anyone saves a policy, and what "restore defaults" returns to. True for anything that moves
/// money, changes permissions, touches keys or whitelists, or loosens a risk control; false for actions whose
/// worst case is a nuisance (a resent notification, an extra screening call).</param>
public sealed record GuardableAction(string Code, string Group, string Label, bool Recommended);

/// <summary>
/// The catalog of actions an admin may mark as requiring a second factor — the vocabulary the settings
/// screen renders and <c>RequireTwoFactor</c> matches against.
///
/// <para><b>Owned by the host, exactly like <see cref="OpsPermissions"/>.</b> These codes name capabilities
/// across Treasury, Withdrawal, Merchant and Compliance, and Identity must not know those modules exist
/// (§4.5) — it stores and compares opaque strings. That separation is also what makes the feature
/// expandable: a new guarded action is a constant here, an entry in <see cref="Guardable"/>, and one
/// <c>.RequireTwoFactor(...)</c> on the route. No schema change, no Identity change, and the settings screen
/// picks it up with no frontend change.</para>
///
/// <para><b>An action code is not a permission code</b>, even where the strings happen to match. A
/// permission asks <em>may this role do it at all</em>; a guarded action asks <em>must this person prove
/// themselves again right now</em>. Keeping the two vocabularies separate means an action can be guarded
/// without inventing a permission, and a permission can be split later without disturbing the policy.</para>
/// </summary>
public static class GuardedActions
{
    // ── custody: company funds moving in or out of the platform's control ──
    public const string TreasuryTopUp = "ops.treasury.top-up";
    public const string TreasuryColdWallet = "ops.treasury.cold-wallet";

    // ── money out ──
    public const string WithdrawalApprove = "ops.withdrawals.approve";
    public const string WithdrawalSettlement = "ops.withdrawals.record-settlement";
    public const string WithdrawalFunding = "ops.withdrawals.funding";

    // ── merchant terms and balances ──
    public const string BalanceAdjust = "ops.balances.adjust";
    public const string MerchantSettlementWallet = "ops.merchants.settlement-wallet";
    public const string MerchantCredential = "ops.merchants.rotate-key";

    /// <summary>
    /// The per-merchant controls that decide whether a payout needs a human at all: the approval threshold
    /// and the merchant's own approval stage.
    ///
    /// <para>Guardable because raising a threshold is a silent way to switch off oversight — a payout that
    /// would have waited for staff simply stops waiting, and nothing about the payout itself looks unusual
    /// afterwards. It belongs in the same class as moving money, not in the same class as editing a fee.</para>
    /// </summary>
    public const string MerchantRiskControls = "ops.merchants.risk-controls";

    /// <summary>The merchant's API IP allowlist — an empty or widened list decides who may call the money
    /// API at all, so it is access control rather than configuration.</summary>
    public const string MerchantAllowedIps = "ops.merchants.allowed-ips";

    /// <summary>Creating or resetting a merchant's portal login: it hands someone access to that merchant's
    /// money-out screens, and the password comes back in the response.</summary>
    public const string MerchantPortalAccount = "ops.merchants.portal-account";

    /// <summary>A merchant's pricing and limits: fees, deposit and withdrawal min/max, the cash-out cap, and
    /// the platform default fee new merchants start from. Every one of them changes how much money moves on
    /// every future transaction, and a quiet edit reads exactly like a normal one afterwards.</summary>
    public const string MerchantPricing = "ops.merchants.pricing";

    /// <summary>The settlement period (T+N) — how long a deposit waits before it can be withdrawn. Shortening
    /// it releases funds earlier than the merchant's agreed terms, so it is a risk control, not a setting.</summary>
    public const string MerchantSettlementPeriod = "ops.merchants.settlement-period";

    /// <summary>Freezing, unfreezing or closing a merchant. Unfreezing restores the ability to move money;
    /// closing ends the merchant's relationship with the platform.</summary>
    public const string MerchantStatus = "ops.merchants.status";

    /// <summary>Creating a merchant or editing its profile (name, callback URL). Not in the recommended
    /// baseline: no money moves and no access is granted by it alone.</summary>
    public const string MerchantProfile = "ops.merchants.profile";

    // ── orders, wallets and callbacks ──

    /// <summary>Manually marking a deposit order failed. It overrides what the chain pipeline would have
    /// decided about a customer's payment.</summary>
    public const string DepositManualFail = "ops.deposits.manual-fail";

    /// <summary>Suspending or resuming a deposit wallet — resuming puts an address back into circulation for
    /// customers to pay into.</summary>
    public const string WalletSuspend = "ops.wallets.suspend";

    /// <summary>Resending a merchant callback. Not in the recommended baseline: it re-delivers a notification
    /// the merchant was already owed and moves no money.</summary>
    public const string CallbackResend = "ops.callbacks.resend";

    /// <summary>Forcing a fresh AML screening. Not in the recommended baseline: it only reads, though every
    /// call spends the screening provider's daily quota.</summary>
    public const string ComplianceRescreen = "ops.compliance.re-screen";

    // ── platform controls ──
    public const string SweepSettings = "ops.sweep.settings";
    public const string CompliancePolicy = "ops.compliance.policy";
    public const string AccountsManage = "ops.accounts.manage";
    public const string RolesManage = "ops.roles.manage";

    /// <summary>
    /// The action that protects the control itself. ALWAYS guarded, and deliberately absent from
    /// <see cref="Guardable"/> so no settings screen can offer to switch it off.
    ///
    /// <para>Without this the control unlocks itself: anyone on a stolen admin session unticks every action
    /// and every code prompt disappears. Forcing it means weakening 2FA always costs a fresh code from a
    /// real authenticator — the same reasoning that makes the shipped sanctions designations add-only in the
    /// screening policy.</para>
    ///
    /// <para>The literal is shared with <see cref="TwoFactorPolicyVersion.SelfProtectingAction"/>, which
    /// Identity owns; it cannot reference this host, so the two are pinned equal by a test.</para>
    /// </summary>
    public const string TwoFactorPolicy = TwoFactorPolicyVersion.SelfProtectingAction;

    /// <summary>
    /// Everything an admin may choose to guard. Explicit rather than reflective, like
    /// <see cref="OpsPermissions.All"/>: an enumerable list is easier to audit, and this file has to stay in
    /// step with the routes anyway — which a test enforces rather than trusting.
    /// </summary>
    public static IReadOnlyList<GuardableAction> Guardable { get; } =
    [
        new(TreasuryTopUp, "Treasury", "Record a hot-wallet top-up", Recommended: true),
        new(TreasuryColdWallet, "Treasury", "Register, activate or retire a cold collection wallet", Recommended: true),
        new(WithdrawalApprove, "Withdrawals", "Approve or reject a payout", Recommended: true),
        new(WithdrawalSettlement, "Withdrawals", "Record an off-system merchant settlement", Recommended: true),
        new(WithdrawalFunding, "Withdrawals", "Release or cancel a payout held for funds", Recommended: true),
        new(BalanceAdjust, "Merchants", "Adjust a merchant balance", Recommended: true),
        new(MerchantSettlementWallet, "Merchants", "Change a merchant settlement wallet", Recommended: true),
        new(MerchantCredential, "Merchants", "Rotate a merchant API credential", Recommended: true),
        new(MerchantRiskControls, "Merchants", "Change a payout approval threshold or approval stage", Recommended: true),
        new(MerchantAllowedIps, "Merchants", "Change a merchant API IP allowlist", Recommended: true),
        new(MerchantPortalAccount, "Merchants", "Create or reset a merchant portal login", Recommended: true),
        new(MerchantPricing, "Merchants", "Change merchant fees, limits or cash-out cap, or the default fee", Recommended: true),
        new(MerchantSettlementPeriod, "Merchants", "Change a merchant settlement period (T+N)", Recommended: true),
        new(MerchantStatus, "Merchants", "Freeze, unfreeze or close a merchant", Recommended: true),
        new(MerchantProfile, "Merchants", "Create a merchant or edit its profile", Recommended: false),
        new(DepositManualFail, "Orders", "Manually mark a deposit order failed", Recommended: true),
        new(WalletSuspend, "Wallets", "Suspend or resume a deposit wallet", Recommended: true),
        new(CallbackResend, "Callbacks", "Resend a merchant callback", Recommended: false),
        new(SweepSettings, "Sweep", "Change sweep settings or trigger a scan", Recommended: true),
        new(CompliancePolicy, "Compliance", "Change screening thresholds", Recommended: true),
        new(ComplianceRescreen, "Compliance", "Force a fresh address screening", Recommended: false),
        new(AccountsManage, "Staff", "Create, disable or reset a staff account", Recommended: true),
        new(RolesManage, "Staff", "Create or change a role", Recommended: true),
    ];

    /// <summary>
    /// The recommended security baseline: what a fresh environment guards before anyone has saved a policy,
    /// and exactly what "restore defaults" saves. Derived from <see cref="Guardable"/> so the two can never
    /// disagree. "Restore defaults" deliberately returns HERE rather than to "nothing guarded" — resetting a
    /// security control should never be the fastest way to switch it off.
    /// </summary>
    public static IReadOnlyList<string> RecommendedCodes { get; } =
        [.. Guardable.Where(a => a.Recommended).Select(a => a.Code)];

    /// <summary>Every code the policy may legitimately contain, the always-on one included. Used to refuse a
    /// saved policy naming an action nothing enforces — a checkbox that guards nothing is worse than no
    /// checkbox, because it reads as protection that is not there.</summary>
    public static IReadOnlyList<string> AllCodes { get; } =
        [.. Guardable.Select(a => a.Code).Append(TwoFactorPolicy)];
}
