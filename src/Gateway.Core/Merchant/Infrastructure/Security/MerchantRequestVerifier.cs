using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;
using System.Net;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Verifies inbound gateway request signatures. Resolves the credential by API key, decrypts its signing
/// secret in-process, and constant-time compares the recomputed HMAC. The secret never leaves this module.
/// </summary>
public sealed class MerchantRequestVerifier(
    IMerchantRepository repository, ISecretCipher secretCipher, ILogger<MerchantRequestVerifier> logger)
    : IMerchantRequestVerifier
{
    public async Task<Result<Guid>> VerifyAsync(
        string apiKey,
        string timestamp,
        string body,
        string signatureHex,
        IPAddress? clientIp,
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

        if (!merchant.CanTransact)
            return Result.Failure<Guid>(MerchantErrors.NotTransactable);

        // Last, so it is reached only with genuine credentials. Logged, because the merchant asking "why 403?" needs
        // the address we saw, and a burst of these from one address is someone holding a leaked key.
        if (!merchant.AllowsApiCallFrom(clientIp))
        {
            logger.LogWarning(
                "Refused API call for merchant {MerchantCode} from {ClientIp}: not on its IP allowlist ({AllowedCount} entries).",
                merchant.MerchantCode, clientIp?.ToString() ?? "unknown", merchant.Configuration.AllowedIps.Count);
            return Result.Failure<Guid>(MerchantErrors.IpNotAllowed);
        }

        return Result.Success(merchant.Id);
    }
}
