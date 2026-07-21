namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

public sealed class StaffAuthOptions
{
    public const string SectionName = "StaffAuth";

    /// <summary>How long a session stays valid after login if never explicitly logged out.</summary>
    public int SessionTtlHours { get; init; } = 8;
}
