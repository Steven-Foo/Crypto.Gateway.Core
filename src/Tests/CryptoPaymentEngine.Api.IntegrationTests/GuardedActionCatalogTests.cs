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
