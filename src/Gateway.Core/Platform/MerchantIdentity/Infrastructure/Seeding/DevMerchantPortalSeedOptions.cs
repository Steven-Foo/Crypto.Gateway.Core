namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Seeding;

public sealed class DevMerchantPortalSeedOptions
{
    public const string SectionName = "MerchantIdentity:DevSeed";

    public bool Enabled { get; init; }

    /// <summary>The dev merchant (by code) this portal login is bound to — the tenant the seeded user can see.</summary>
    public string MerchantCode { get; init; } = "";

    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Further portal logins, one per additional demo tenant — so portal UI work can sign in as each merchant
    /// and confirm that tenant isolation actually holds (a single-tenant dev environment cannot show a
    /// cross-tenant leak, which is the one portal defect that matters most). Empty by default.
    /// </summary>
    public IReadOnlyList<DevMerchantPortalLogin> AdditionalLogins { get; init; } = [];
}

/// <summary>One dev portal login bound to one merchant tenant. Never a real credential (§10).</summary>
public sealed class DevMerchantPortalLogin
{
    public string MerchantCode { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string DisplayName { get; init; } = "";
}
