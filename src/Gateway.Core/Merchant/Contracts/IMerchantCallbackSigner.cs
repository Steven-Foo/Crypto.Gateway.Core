using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>The timestamp and hex signature to attach to an outbound callback as <c>X-Timestamp</c>/<c>X-Signature</c>.</summary>
public sealed record CallbackSignature(string Timestamp, string SignatureHex);

/// <summary>
/// Signs an outbound callback body for a merchant using the same key and construction as inbound
/// verification (<see cref="IMerchantRequestVerifier"/>), so the merchant validates it with the secret they
/// already hold. Used by the notification/callback path; the signing secret never leaves the Merchant module.
/// </summary>
public interface IMerchantCallbackSigner
{
    /// <summary>
    /// Produces <c>HMAC-SHA256("{timestamp}\n{body}")</c> with the merchant's current active signing secret.
    /// Fails with <c>merchant.credential_not_found</c> if the merchant has no active credential.
    /// </summary>
    Task<Result<CallbackSignature>> SignAsync(Guid merchantId, string body, CancellationToken cancellationToken = default);
}
