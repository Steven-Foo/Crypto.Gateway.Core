using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// Verifies an inbound gateway request signature without ever handing the signing secret across a module
/// boundary (§10). A host's auth filter passes the wire values; the Merchant module resolves the
/// credential, decrypts its signing secret internally, recomputes <c>HMAC-SHA256("{timestamp}\n{body}")</c>,
/// and constant-time compares it. This mirrors the partner's existing scheme exactly, so their SDKs and
/// merchants integrate unchanged (the secret is 64 hex chars → a 32-byte HMAC key).
///
/// The <em>freshness</em> of the timestamp (replay window) is a transport concern the caller enforces; this
/// port answers only "is this signature authentic for a transactable merchant?".
/// </summary>
public interface IMerchantRequestVerifier
{
    /// <summary>
    /// Returns the owning merchant id when the signature is authentic and the merchant can transact.
    /// Failure is deliberately uniform (<c>merchant.invalid_credentials</c>) for unknown key / bad
    /// signature so callers cannot probe which part failed.
    /// </summary>
    Task<Result<Guid>> VerifyAsync(
        string apiKey,
        string timestamp,
        string body,
        string signatureHex,
        CancellationToken cancellationToken = default);
}
