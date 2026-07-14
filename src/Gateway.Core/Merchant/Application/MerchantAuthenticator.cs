using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

public interface IMerchantAuthenticator
{
    Task<Result<Guid>> AuthenticateAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default);
}

public sealed class MerchantAuthenticator(IMerchantRepository repository, IApiSecretHasher hasher) : IMerchantAuthenticator
{
    /// <summary>
    /// A syntactically valid hash used only to burn equivalent CPU when the API key is unknown.
    /// Without it, "unknown key" returns measurably faster than "known key, wrong secret", which
    /// leaks which API keys exist.
    /// </summary>
    private const string DummyHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public async Task<Result<Guid>> AuthenticateAsync(
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        var credential = await repository.FindActiveCredentialAsync(apiKey, cancellationToken);

        if (credential is null)
        {
            hasher.Verify(apiSecret, DummyHash, hasher.CurrentVersion);
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);
        }

        if (!hasher.Verify(apiSecret, credential.SecretHash, credential.HashVersion))
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        var merchant = await repository.GetByIdAsync(credential.MerchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<Guid>(MerchantErrors.InvalidCredentials);

        return merchant.CanTransact
            ? Result.Success(merchant.Id)
            : Result.Failure<Guid>(MerchantErrors.NotTransactable);
    }
}
