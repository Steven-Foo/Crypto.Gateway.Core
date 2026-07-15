using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Verifies inbound gateway request signatures. Resolves the credential by API key, decrypts its signing
/// secret in-process, and constant-time compares the recomputed HMAC. The secret never leaves this module.
/// </summary>
public sealed class MerchantRequestVerifier(IMerchantRepository repository, ISecretCipher secretCipher)
    : IMerchantRequestVerifier
{
    public async Task<Result<Guid>> VerifyAsync(
        string apiKey,
        string timestamp,
        string body,
        string signatureHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(timestamp))
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        var credential = await repository.FindActiveCredentialAsync(apiKey, cancellationToken);
        if (credential is null || string.IsNullOrEmpty(credential.SigningSecretCipher))
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        string signingSecret;
        try
        {
            signingSecret = secretCipher.Unprotect(credential.SigningSecretCipher);
        }
        catch (CryptographicException)
        {
            // A key we can't decrypt is an operational fault, but to the caller it is simply "no".
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);
        }

        var expected = MerchantHmac.ComputeHex(signingSecret, $"{timestamp}\n{body}");
        if (!MerchantHmac.FixedTimeEqualsHex(expected, signatureHex))
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        // Signature authentic — now the merchant must be allowed to transact.
        var merchant = await repository.GetByIdAsync(credential.MerchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        return merchant.CanTransact
            ? Result.Success(merchant.Id)
            : Result.Failure<Guid>(MerchantErrors.NotTransactable);
    }
}
