namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

/// <summary>One merchant-portal action that can be made to require a fresh authenticator code.</summary>
/// <param name="Code">The opaque string stored in a policy version and matched by the portal's filter.</param>
/// <param name="Group">Which part of the portal it belongs to, so a settings screen can group the toggles.</param>
/// <param name="Label">What to call it to a human (English — the frontends translate by <c>Code</c>).</param>
/// <param name="Recommended">Part of the recommended platform minimum: what applies before staff save one, and
/// what "restore defaults" returns to.</param>
public sealed record MerchantGuardableAction(string Code, string Group, string Label, bool Recommended);

/// <summary>
/// The catalog of merchant-portal actions that can require a second factor.
///
/// <para><b>Why it lives in the module and not the portal host</b> (unlike the Ops catalog, which the Ops host
/// owns): TWO hosts need it. The merchant portal enforces it, and the admin back office sets the platform
/// minimum from it. A host cannot reference another host, so the list sits with the module that owns portal
/// identity. The codes are still opaque here — nothing in this module interprets what "a payout" is (§4.5); a
/// drift test in the test suite checks every code is actually enforced on a portal route.</para>
///
/// <para>Adding an action: a constant, an entry in <see cref="Guardable"/>, and one
/// <c>.RequirePortalTwoFactor(...)</c> on the portal route. Both settings screens pick it up automatically.</para>
/// </summary>
public static class MerchantGuardedActions
{
    // ── money out ──
    public const string PayoutCreate = "portal.payouts.create";
    public const string PayoutApprove = "portal.payouts.approve";
    public const string CashOutCreate = "portal.cashout.create";

    // ── money in ──
    /// <summary>Creating a top-up invoice. Not recommended by default: no money leaves, and real crypto must
    /// actually arrive — though a top-up skips the T+N hold, which is why it is offered at all.</summary>
    public const string TopUpCreate = "portal.topup.create";

    // ── API access ──
    public const string ApiKeyRotate = "portal.api.rotate-key";
    public const string AllowedIps = "portal.api.allowed-ips";

    // ── team ──
    public const string AccountsManage = "portal.accounts.manage";
    public const string RolesManage = "portal.roles.manage";

    /// <summary>A user changing their OWN password. Not recommended by default: it already requires the
    /// current password.</summary>
    public const string ChangeOwnPassword = "portal.account.change-password";

    /// <summary>
    /// Changing which actions require 2FA. ALWAYS guarded — for the merchant's own settings page — and
    /// deliberately absent from <see cref="Guardable"/>, so no settings screen can offer to switch it off.
    /// Without it a stolen merchant-admin session could untick every extra protection the merchant chose.
    /// (It cannot touch the platform minimum either way; that is only editable from the admin back office.)
    /// </summary>
    public const string TwoFactorPolicy = "portal.security.two-factor-policy";

    public static IReadOnlyList<MerchantGuardableAction> Guardable { get; } =
    [
        new(PayoutCreate, "Payouts", "Submit a payout", Recommended: true),
        new(PayoutApprove, "Payouts", "Approve or reject a payout", Recommended: true),
        new(CashOutCreate, "Cash-out", "Submit a cash-out to the settlement wallet", Recommended: true),
        new(TopUpCreate, "Top-up", "Create a top-up invoice", Recommended: false),
        new(ApiKeyRotate, "API", "Rotate the API key", Recommended: true),
        new(AllowedIps, "API", "Change the API IP whitelist", Recommended: true),
        new(AccountsManage, "Team", "Create, disable or reset a user (including their 2FA)", Recommended: true),
        new(RolesManage, "Team", "Create or change a role", Recommended: true),
        new(ChangeOwnPassword, "Account", "Change my own password", Recommended: false),
    ];

    /// <summary>The recommended platform minimum. Derived from <see cref="Guardable"/>, so the flag and the set
    /// cannot disagree.</summary>
    public static IReadOnlyList<string> RecommendedCodes { get; } =
        [.. Guardable.Where(a => a.Recommended).Select(a => a.Code)];

    public static bool IsGuardable(string code) =>
        Guardable.Any(a => string.Equals(a.Code, code, StringComparison.Ordinal));
}
