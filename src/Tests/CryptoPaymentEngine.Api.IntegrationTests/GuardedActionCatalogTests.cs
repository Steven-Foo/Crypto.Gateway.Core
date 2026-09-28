using CryptoPaymentEngine.Api.OperationsApi.Endpoints;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Api.IntegrationTests;

/// <summary>
/// The drift guard for two-factor guarded actions.
///
/// <para><b>Why this test is load-bearing.</b> The settings screen renders
/// <see cref="GuardedActions.Guardable"/> as a list of checkboxes. If a code is in that list but no route
/// carries <c>.RequireTwoFactor</c> with it, an admin ticks a box, the UI reports the action as protected,
/// and it is not. A control that appears to be on and is off is worse than one that is visibly off, because
/// nobody goes looking for it. The same class of guard as the effective-status test on the withdrawal
/// directory.</para>
///
/// <para>It works by reading the endpoint source rather than by reflecting over the route table: building
/// the real host needs a database, and this must run everywhere the rest of the suite does.</para>
/// </summary>
public class GuardedActionCatalogTests
{
    private static readonly string EndpointsDirectory = LocateEndpointsDirectory();

    private static string LocateEndpointsDirectory()
    {
        // Walk up to the repository root (the folder holding the solution), so this does not depend on the
        // build output layout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CryptoPaymentEngine.sln")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root from the test output directory.");

        var endpoints = Path.Combine(
            directory!.FullName, "src", "Api", "OperationsApi", "CryptoPaymentEngine.Api.OperationsApi", "Endpoints");

        Directory.Exists(endpoints).ShouldBeTrue($"Expected the Ops endpoints at '{endpoints}'.");
        return endpoints;
    }

    private static string AllEndpointSource() =>
        string.Concat(Directory.EnumerateFiles(EndpointsDirectory, "*.cs").Select(File.ReadAllText));

    public static TheoryData<string> GuardableCodes()
    {
        var data = new TheoryData<string>();
        foreach (var action in GuardedActions.Guardable)
            data.Add(action.Code);

        return data;
    }

    [Theory]
    [MemberData(nameof(GuardableCodes))]
    public void Every_guardable_action_is_enforced_on_at_least_one_route(string code)
    {
        var name = GuardedActions.Guardable.First(a => a.Code == code);

        // The constant's C# name, which is how the route references it.
        var constantName = typeof(GuardedActions)
            .GetFields()
            .Where(f => f.IsLiteral && (string?)f.GetRawConstantValue() == code)
            .Select(f => f.Name)
            .FirstOrDefault();

        constantName.ShouldNotBeNull($"'{code}' is in the Guardable catalog but has no constant on GuardedActions.");

        AllEndpointSource()
            .ShouldContain(
                $"RequireTwoFactor(GuardedActions.{constantName})",
                Case.Sensitive,
                $"'{name.Label}' ({code}) can be ticked in the settings UI but no route enforces it. " +
                "Either add .RequireTwoFactor(...) to the route, or remove it from GuardedActions.Guardable.");
    }

    /// <summary>
    /// The self-protecting action must stay OFF the guardable list: it is always enforced, and offering it
    /// as a checkbox would imply it can be switched off.
    /// </summary>
    [Fact]
    public void The_policy_action_is_not_offered_as_a_choice()
    {
        GuardedActions.Guardable.ShouldNotContain(a => a.Code == GuardedActions.TwoFactorPolicy);
        GuardedActions.AllCodes.ShouldContain(GuardedActions.TwoFactorPolicy);
    }

    /// <summary>
    /// Identity owns the literal (it enforces it when saving a policy) and the host owns the catalog; the
    /// module may not reference the host, so the two constants are pinned equal here instead. If they ever
    /// diverge, a saved policy would guard one string while the filter checked another — the control would
    /// silently stop protecting itself.
    /// </summary>
    [Fact]
    public void The_policy_action_literal_agrees_with_the_identity_module()
    {
        GuardedActions.TwoFactorPolicy.ShouldBe(TwoFactorPolicyVersion.SelfProtectingAction);
    }

    /// <summary>The policy endpoint itself must carry the guard, or the one route that must never be
    /// unprotected would be unprotected.</summary>
    [Fact]
    public void The_policy_endpoint_is_guarded()
    {
        AllEndpointSource().ShouldContain("RequireTwoFactor(GuardedActions.TwoFactorPolicy)");
    }

    /// <summary>
    /// The recommended baseline is what a fresh environment guards and what "restore defaults" saves. Only
    /// the three actions whose worst case is a nuisance (no money moves, no access granted) are left out —
    /// anything else dropping out of it would silently weaken every fresh deployment and every reset.
    /// </summary>
    [Fact]
    public void The_recommended_baseline_is_everything_except_the_low_risk_actions()
    {
        string[] notRecommended =
        [
            GuardedActions.MerchantProfile,
            GuardedActions.CallbackResend,
            GuardedActions.ComplianceRescreen,
        ];

        GuardedActions.Guardable.Where(a => !a.Recommended).Select(a => a.Code)
            .ShouldBe(notRecommended, ignoreOrder: true);

        GuardedActions.RecommendedCodes.ShouldBe(
            GuardedActions.Guardable.Where(a => a.Recommended).Select(a => a.Code), ignoreOrder: true);

        // The money, permission, key and whitelist actions the page calls out explicitly.
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.WithdrawalApprove);
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.BalanceAdjust);
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.RolesManage);
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.MerchantCredential);
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.MerchantAllowedIps);
        GuardedActions.RecommendedCodes.ShouldContain(GuardedActions.MerchantPricing);
    }

    /// <summary>Codes are stored in saved policies — a duplicate would render as two checkboxes controlling
    /// one rule.</summary>
    [Fact]
    public void Every_guardable_code_is_unique()
    {
        GuardedActions.Guardable.Select(a => a.Code).ShouldBeUnique();
    }

    /// <summary>"Restore defaults" is a policy save, so it must carry the same always-on guard as the save —
    /// otherwise it would be the one way to rewrite the policy without a fresh code.</summary>
    [Fact]
    public void The_restore_defaults_endpoint_is_guarded()
    {
        var source = File.ReadAllText(Path.Combine(EndpointsDirectory, "OpsTwoFactorEndpoints.cs"));
        var statement = System.Text.RegularExpressions.Regex.Match(
            source, @"app\.MapPost\(""/api/v1/ops/two-factor/policy/restore-defaults"".*?;",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;

        statement.ShouldNotBeNullOrEmpty();
        statement.ShouldContain("RequireTwoFactor(GuardedActions.TwoFactorPolicy)");
    }

    /// <summary>
    /// Found by exercising the policy save over HTTP: the audit reason used to spell out the full before AND
    /// after lists, which overflowed the 512-character audit column once the catalog grew — the policy saved,
    /// then the audit write failed and the operator got a 500 for a change that had happened. The worst case
    /// is every action switched on from nothing, plus a maximum-length note.
    /// </summary>
    [Fact]
    public void The_policy_change_audit_reason_always_fits_the_audit_column()
    {
        var everything = GuardedActions.AllCodes;
        var longNote = new string('x', 512);

        OpsTwoFactorEndpoints.DescribePolicyChange([], everything, longNote).Length
            .ShouldBeLessThanOrEqualTo(OpsTwoFactorEndpoints.AuditReasonMaxLength);
        OpsTwoFactorEndpoints.DescribePolicyChange(everything, [], longNote).Length
            .ShouldBeLessThanOrEqualTo(OpsTwoFactorEndpoints.AuditReasonMaxLength);
    }

    /// <summary>The reason records what CHANGED — the part a reviewer needs — not the unchanged remainder.</summary>
    [Fact]
    public void The_policy_change_audit_reason_lists_only_what_changed()
    {
        var reason = OpsTwoFactorEndpoints.DescribePolicyChange(
            [GuardedActions.BalanceAdjust, GuardedActions.RolesManage],
            [GuardedActions.BalanceAdjust, GuardedActions.MerchantPricing],
            "tighten pricing");

        reason.ShouldContain($"Added: {GuardedActions.MerchantPricing}.");
        reason.ShouldContain($"Removed: {GuardedActions.RolesManage}.");
        reason.ShouldNotContain(GuardedActions.BalanceAdjust);
        reason.ShouldEndWith("Note: tighten pricing");
    }

    /// <summary>
    /// Every code referenced by a route must exist in the catalog. A route guarding a code no settings
    /// screen can offer is a permanently-off control that looks deliberate in the source.
    /// </summary>
    [Fact]
    public void No_route_guards_an_action_missing_from_the_catalog()
    {
        var referenced = System.Text.RegularExpressions.Regex
            .Matches(AllEndpointSource(), @"RequireTwoFactor\(GuardedActions\.(\w+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        var known = typeof(GuardedActions).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var constant in referenced)
            known.ShouldContain(constant);
    }

    /// <summary>
    /// Ordering check: the permission gate must be added BEFORE the two-factor gate on every guarded route.
    /// Filters run in registration order, and the wrong order would demand a code from someone who may not
    /// perform the action at all — confirming the action exists to a caller with no access to it.
    /// </summary>
    [Fact]
    public void Two_factor_is_always_chained_after_the_permission_gate()
    {
        foreach (var file in Directory.EnumerateFiles(EndpointsDirectory, "*.cs"))
        {
            var source = File.ReadAllText(file);

            // Each registration is one statement: app.Map...( ... ) [.Require...]* ;
            foreach (var statement in System.Text.RegularExpressions.Regex.Matches(
                         source, @"app\.Map\w+\(.*?;", System.Text.RegularExpressions.RegexOptions.Singleline)
                     .Select(m => m.Value))
            {
                var twoFactor = statement.IndexOf("RequireTwoFactor(", StringComparison.Ordinal);
                if (twoFactor < 0)
                    continue;

                var permission = statement.IndexOf("RequirePermission(", StringComparison.Ordinal);

                // A route with no permission gate has nothing to order against — self-scoped routes (a
                // caller acting on their own account) deliberately carry none. What must never happen is a
                // permission gate placed AFTER the code prompt, which would demand a code from someone who
                // may not perform the action at all, confirming it exists to a caller with no access.
                if (permission < 0)
                    continue;

                permission.ShouldBeLessThan(
                    twoFactor,
                    $"{Path.GetFileName(file)}: RequireTwoFactor must be chained AFTER RequirePermission in " +
                    $"'{statement.Split('\n')[0].Trim()}'.");
            }
        }
    }
}
