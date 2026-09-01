namespace CryptoPaymentEngine.Api.MerchantGateway.Development;

/// <summary>
/// DEV/TESTNET ONLY. Controls the demo data portfolio seeded by <see cref="DevSampleDataSeeder"/> so the
/// back-office and merchant-portal UIs have something realistic to render. Opt-in
/// (<see cref="Enabled"/> defaults to false) — an existing dev run is unchanged unless a developer asks for it.
/// </summary>
public sealed class DevSampleDataOptions
{
    public const string SectionName = "DevSampleData";

    /// <summary>Off by default. Never true in production — the host only binds this in the testnet tier (§10).</summary>
    public bool Enabled { get; set; }

    /// <summary>Where the demo merchants' callbacks are posted. Defaults to the in-host sink so a developer can
    /// see the signed callback at <c>GET /dev/callbacks</c> without running anything else.</summary>
    public string CallbackUrl { get; set; } = "http://localhost:51078/dev/callbacks";

    /// <summary>How long to wait for the deposit workers to credit the seeded deposits before giving up on the
    /// money-out half. The deposits still land; only the withdrawals are skipped, and a re-run completes them.</summary>
    public int LedgerWaitSeconds { get; set; } = 180;
}
