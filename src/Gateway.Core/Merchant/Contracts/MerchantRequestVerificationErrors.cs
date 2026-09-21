namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// Error codes from <see cref="IMerchantRequestVerifier"/> that a host has to answer differently from the uniform
/// 401. Published here so a host can branch on them without reaching into the module's domain (§4.5).
/// </summary>
public static class MerchantRequestVerificationErrors
{
    /// <summary>
    /// The key and signature were authentic, but the call came from an address that is not on the merchant's IP
    /// allowlist (an empty allowlist permits no address). Answer 403: the credentials are fine, the caller is not.
    /// Reachable only with a genuine key AND signing secret, so it tells a prober nothing.
    /// </summary>
    public const string IpNotAllowed = "merchant.ip_not_allowed";
}
