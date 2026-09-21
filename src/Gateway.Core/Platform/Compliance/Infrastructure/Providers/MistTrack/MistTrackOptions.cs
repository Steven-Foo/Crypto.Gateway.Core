namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;

/// <summary>
/// MistTrack (SlowMist) connection settings, bound from <c>Compliance:MistTrack</c>.
/// The API key is a credential and belongs in a git-ignored local file or a secret store, never in a
/// committed appsettings (§10).
/// </summary>
public sealed class MistTrackOptions
{
    public const string SectionName = "Compliance:MistTrack";

    /// <summary>
    /// Live API. Point this at <c>https://sandbox-api.misttrack.io</c> to run against the sandbox, which
    /// serves graded fixture addresses and does not consume the live plan's daily quota.
    /// </summary>
    public string BaseUrl { get; set; } = "https://sandbox-api.misttrack.io";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Requests per second the adapter will issue. The Standard plan permits 1/sec and 10,000/day; exceeding
    /// it earns a 429 with a <c>retry_after</c>, so the limiter here is what keeps a burst of payouts from
    /// converting into a wall of failed screenings.
    /// </summary>
    public double RequestsPerSecond { get; set; } = 1;

    public int TimeoutSeconds { get; set; } = 15;
}
