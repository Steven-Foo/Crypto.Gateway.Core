using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// The one and only time the secrets are ever readable. <see cref="ApiSecret"/> is the bearer secret;
/// <see cref="SigningSecret"/> is the request/callback HMAC key the merchant signs with. Neither is stored
/// recoverably except the signing secret's encrypted-at-rest form; callers must not log either. If the
/// merchant loses them they rotate — the bearer secret has no recovery, that is the point.
/// </summary>
public sealed record MerchantRegistrationResult(
    Guid MerchantId, string MerchantCode, string ApiKey, string ApiSecret, string SigningSecret);

public interface IMerchantRegistrar
{
    Task<Result<MerchantRegistrationResult>> RegisterAsync(
        string merchantCode,
        string name,
        string? callbackUrl,
        CancellationToken cancellationToken = default);
}

public sealed class MerchantRegistrar(
    IMerchantRepository repository,
    IApiCredentialGenerator generator,
    IApiSecretHasher hasher,
    ISecretCipher secretCipher,
    TimeProvider timeProvider) : IMerchantRegistrar
{
    public async Task<Result<MerchantRegistrationResult>> RegisterAsync(
        string merchantCode,
        string name,
        string? callbackUrl,
        CancellationToken cancellationToken = default)
    {
        var merchantResult = Domain.Merchant.Create(merchantCode, name, callbackUrl, timeProvider);
        if (merchantResult.IsFailure)
            return Result.Failure<MerchantRegistrationResult>(merchantResult.Error!);

        var merchant = merchantResult.Value;

        // Pre-check for a friendly error. The UNIQUE index on MerchantCode remains the real
        // arbiter — two concurrent registrations will still collide there, by design.
        if (await repository.CodeExistsAsync(merchant.MerchantCode, cancellationToken))
            return Result.Failure<MerchantRegistrationResult>(MerchantErrors.CodeAlreadyExists);

        var credential = generator.Generate();
        var secretHash = hasher.Hash(credential.Secret);
        var signingSecretCipher = secretCipher.Protect(credential.SigningSecret);

        var issueResult = merchant.IssueCredential(
            credential.ApiKey, secretHash, hasher.CurrentVersion, signingSecretCipher, timeProvider.GetUtcNow());

        if (issueResult.IsFailure)
            return Result.Failure<MerchantRegistrationResult>(issueResult.Error!);

        repository.Add(merchant);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantRegistrationResult(
            merchant.Id, merchant.MerchantCode, credential.ApiKey, credential.Secret, credential.SigningSecret));
    }
}
