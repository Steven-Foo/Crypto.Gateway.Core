namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

/// <summary>
/// The plaintext <paramref name="Secret"/> and <paramref name="SigningSecret"/> exist only in memory, only
/// on the response to the issuing call. Neither is persisted in the clear and neither must ever be logged (§10).
///
/// <paramref name="Secret"/> is the bearer secret (base64url), stored one-way hashed.
/// <paramref name="SigningSecret"/> is the request/callback HMAC key (64 hex chars → 32 bytes), stored
/// encrypted-but-recoverable because the partner's signing scheme needs the server to hold it.
/// </summary>
public readonly record struct GeneratedApiCredential(string ApiKey, string Secret, string SigningSecret);

public interface IApiCredentialGenerator
{
    GeneratedApiCredential Generate();
}
