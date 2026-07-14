namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

/// <summary>
/// The plaintext <paramref name="Secret"/> exists only in memory, only on the response to the
/// issuing call. It is never persisted and must never be logged (§10).
/// </summary>
public readonly record struct GeneratedApiCredential(string ApiKey, string Secret);

public interface IApiCredentialGenerator
{
    GeneratedApiCredential Generate();
}
