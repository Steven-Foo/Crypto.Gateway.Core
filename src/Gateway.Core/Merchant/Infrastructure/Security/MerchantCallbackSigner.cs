using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Signs outbound callbacks with the merchant's current active signing secret, using the same HMAC
/// construction as inbound verification so a merchant validates the callback with a key they already hold.
/// </summary>
public sealed class MerchantCallbackSigner(
    IMerchantRepository repository,
    ISecretCipher secretCipher,
    TimeProvider timeProvider) : IMerchantCallbackSigner
{
    public async Task<Result<CallbackSignature>> SignAsync(
        Guid merchantId, string body, CancellationToken cancellationToken = default)
    {
        var credential = await repository.FindActiveCredentialByMerchantAsync(merchantId, cancellationToken);
        if (credential is null || string.IsNullOrEmpty(credential.SigningSecretCipher))
            return Result.Failure<CallbackSignature>(MerchantErrors.CredentialNotFound);

        string signingSecret;
        try
        {
            signingSecret = secretCipher.Unprotect(credential.SigningSecretCipher);
        }
        catch (CryptographicException)
        {
            return Result.Failure<CallbackSignature>(MerchantErrors.CredentialNotFound);
        }

        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString();
        var signature = MerchantHmac.ComputeHex(signingSecret, $"{timestamp}\n{body}");
        return Result.Success(new CallbackSignature(timestamp, signature));
    }
}
