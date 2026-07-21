namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Seeding;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Fixed Admin credentials (section <c>StaffAuth:DevSeed</c>) so the next engineer
/// can call <c>POST /api/v1/ops/auth/login</c> on a fresh clone with no bootstrap step. Override in a
/// git-ignored <c>appsettings.Local.json</c>. Never a real credential (§10) — same convention as
/// <c>DevMerchantSeedOptions</c>.
/// </summary>
public sealed class DevStaffSeedOptions
{
    public const string SectionName = "StaffAuth:DevSeed";

    /// <summary>Master switch. When false the seeder does nothing.</summary>
    public bool Enabled { get; init; }

    public string Username { get; init; } = string.Empty;

    /// <summary>Plaintext, dev-only — hashed the same way a real registration would hash it.</summary>
    public string Password { get; init; } = string.Empty;
}
