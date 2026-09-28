using System.Text.RegularExpressions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Api.IntegrationTests;

/// <summary>
/// The drift guard for merchant-portal guarded actions — the portal twin of <see cref="GuardedActionCatalogTests"/>.
/// Both settings screens (the merchant's own, and the platform minimum in the admin back office) render
/// <see cref="MerchantGuardedActions.Guardable"/> as toggles; a code with no route enforcing it would show as
/// protection that does not exist. Reads the portal endpoint source, so it runs without a database.
/// </summary>
public class MerchantGuardedActionCatalogTests
{
    private static readonly string EndpointsDirectory = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CryptoPaymentEngine.sln")))
            directory = directory.Parent;

        directory.ShouldNotBeNull();
        var endpoints = Path.Combine(
            directory!.FullName, "src", "Api", "MerchantPortalApi", "CryptoPaymentEngine.Api.MerchantPortalApi", "Endpoints");
        Directory.Exists(endpoints).ShouldBeTrue($"Expected the portal endpoints at '{endpoints}'.");
        return endpoints;
    }

    private static string Source() =>
        string.Concat(Directory.EnumerateFiles(EndpointsDirectory, "*.cs").Select(File.ReadAllText));

    private static string ConstantName(string code) =>
        typeof(MerchantGuardedActions).GetFields()
            .Where(f => f.IsLiteral && (string?)f.GetRawConstantValue() == code)
            .Select(f => f.Name)
            .Single();

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var a in MerchantGuardedActions.Guardable)
            data.Add(a.Code);
        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_guardable_portal_action_is_enforced_on_a_route(string code) =>
        Source().ShouldContain(
            $"RequirePortalTwoFactor(MerchantGuardedActions.{ConstantName(code)})",
            Case.Sensitive,
            $"'{code}' can be switched on in a settings screen but no portal route enforces it.");

    [Fact]
    public void Both_policy_writes_carry_the_always_on_guard()
    {
        var source = File.ReadAllText(Path.Combine(EndpointsDirectory, "PortalTwoFactorPolicyEndpoints.cs"));
        foreach (var route in new[] { @"app\.MapPut\(""/api/v1/portal/two-factor/policy""", @"app\.MapPost\(""/api/v1/portal/two-factor/policy/restore-defaults""" })
        {
            var statement = Regex.Match(source, route + ".*?;", RegexOptions.Singleline).Value;
            statement.ShouldNotBeNullOrEmpty();
            statement.ShouldContain("RequirePortalTwoFactor(MerchantGuardedActions.TwoFactorPolicy)");
        }
    }

    [Fact]
    public void The_always_on_action_is_not_offered_as_a_toggle() =>
        MerchantGuardedActions.Guardable.ShouldNotContain(a => a.Code == MerchantGuardedActions.TwoFactorPolicy);

    [Fact]
    public void Every_guardable_code_is_unique() =>
        MerchantGuardedActions.Guardable.Select(a => a.Code).ShouldBeUnique();

    /// <summary>Filters run in registration order: the permission gate must come first, or a code would be
    /// demanded from someone who may not perform the action at all.</summary>
    [Fact]
    public void Two_factor_is_chained_after_the_permission_gate()
    {
        foreach (var file in Directory.EnumerateFiles(EndpointsDirectory, "*.cs"))
        {
            foreach (var statement in Regex.Matches(File.ReadAllText(file), @"app\.Map\w+\(.*?;", RegexOptions.Singleline)
                         .Select(m => m.Value))
            {
                var twoFactor = statement.IndexOf("RequirePortalTwoFactor(", StringComparison.Ordinal);
                var permission = statement.IndexOf("RequirePortalPermission(", StringComparison.Ordinal);
                if (twoFactor < 0 || permission < 0)
                    continue;

                permission.ShouldBeLessThan(twoFactor, $"{Path.GetFileName(file)}: '{statement.Split('\n')[0].Trim()}'");
            }
        }
    }
}
