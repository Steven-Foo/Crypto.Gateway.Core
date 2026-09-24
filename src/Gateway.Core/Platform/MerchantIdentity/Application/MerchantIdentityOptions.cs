namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed class MerchantIdentityOptions
{
    public const string SectionName = "MerchantIdentity";

    /// <summary>How long a merchant-portal session stays valid after login if never explicitly logged out.</summary>
    public int SessionTtlHours { get; init; } = 8;

    /// <summary>
    /// The label an authenticator app shows for a portal account. Deliberately distinct from the staff
    /// issuer, so a person who holds both a staff and a merchant login can tell the two entries apart —
    /// identical labels are how someone types the wrong code and concludes 2FA is broken.
    /// </summary>
    public string TwoFactorIssuer { get; init; } = "CryptoPaymentEngine Merchant";
}
