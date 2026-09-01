namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed class MerchantIdentityOptions
{
    public const string SectionName = "MerchantIdentity";

    /// <summary>How long a merchant-portal session stays valid after login if never explicitly logged out.</summary>
    public int SessionTtlHours { get; init; } = 8;
}
