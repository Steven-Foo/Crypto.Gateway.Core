namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Seeding;

/// <summary>
/// DEVELOPMENT / LOCAL ONLY. Binds the fixed credentials for a seeded test merchant (section
/// <c>Merchant:DevSeed</c>) so the next engineer can sign an <c>/api/v1</c> request and drive the full
/// deposit round-trip on a fresh clone — no registration call, no capturing one-time secrets from logs.
///
/// The values are deterministic and documented, so they survive restarts. Override them in a git-ignored
/// <c>appsettings.Local.json</c>. This is a throwaway dev merchant with no real funds; its
/// <see cref="SigningSecret"/> must never be a production merchant's key (§10).
/// </summary>
public sealed class DevMerchantSeedOptions
{
    public const string SectionName = "Merchant:DevSeed";

    /// <summary>Master switch. When false the seeder does nothing.</summary>
    public bool Enabled { get; init; }

    /// <summary>Uppercased to <c>[A-Z0-9_-]{3,64}</c>. Also the idempotency key (unique merchant code).</summary>
    public string MerchantCode { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Optional default callback URL for the merchant. Absolute http/https, or null.</summary>
    public string? CallbackUrl { get; init; }

    /// <summary>Public identifier sent as <c>X-Api-Key</c>. Stored in clear.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Bearer secret. Hashed at rest; not part of the request-signature path, but the column is required.</summary>
    public string ApiSecret { get; init; } = string.Empty;

    /// <summary>The HMAC key the client signs with — 64 lower-case hex chars (a 32-byte key), matching the partner scheme.</summary>
    public string SigningSecret { get; init; } = string.Empty;
}
