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

/// <summary>BO read model — deliberately richer than the public <c>IMerchantDirectory</c> Contract (which
/// never exposes credential presence), since this is for staff, not other modules.</summary>
public sealed record MerchantAdminView(
    Guid MerchantId,
    string MerchantCode,
    string Name,
    string Status,
    int SettlementDelayDays,
    DateTimeOffset CreatedAt,
    bool HasActiveCredential,
    IReadOnlyList<string> AllowedIps,
    IReadOnlyList<MerchantSettlementWalletView> SettlementWallets);

/// <summary>A merchant's whitelisted cash-out destination for a chain, for staff read-back.</summary>
public sealed record MerchantSettlementWalletView(string Chain, string Address);

public interface IMerchantRegistrar
{
    Task<Result<MerchantRegistrationResult>> RegisterAsync(
        string merchantCode,
        string name,
        string? callbackUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unfreezes a <c>Frozen</c> merchant back to <c>Active</c>. A newly-registered merchant is already
    /// <c>Active</c> (no separate approval step) — this exists purely to reverse <see cref="FreezeAsync"/>.
    /// </summary>
    Task<Result> ActivateAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Admin risk-hold — blocks all transacting for the merchant. Reversible: <see cref="ActivateAsync"/>
    /// unfreezes it. Matches APIGateway's merchant status toggle, expressed against this project's status enum.</summary>
    Task<Result> FreezeAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Sets the merchant's settlement period (T+N) in whole days (0 = T+0). Gates the withdrawable
    /// balance for BOTH user payouts and the merchant cash-out. The domain validates the 0–30 range.</summary>
    Task<Result> SetSettlementDelayAsync(Guid merchantId, int days, CancellationToken cancellationToken = default);

    /// <summary>Registers/updates the merchant's settlement (cash-out) wallet for a chain — the whitelisted
    /// destination of a Merchant Withdrawal, never client-supplied (§10). One per chain.</summary>
    Task<Result> SetSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// The "change password" equivalent for a merchant: merchants don't log in here, they authenticate by
    /// API credential, so rotating means revoking the current active credential and issuing a fresh one —
    /// same effect (old secret stops working immediately) via this project's real mechanism, matching
    /// APIGateway's <c>RegenerateKey</c>.
    /// </summary>
    Task<Result<MerchantRegistrationResult>> RotateCredentialAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the merchant's IP allowlist. IP format validation is the caller's job (the host
    /// edge) — this only persists and diffs, matching APIGateway's <c>UpdateAllowedIps</c>.</summary>
    Task<Result<AllowedIpsChange>> UpdateAllowedIpsAsync(
        Guid merchantId, IReadOnlyCollection<string> validIps, CancellationToken cancellationToken = default);

    Task<Result<MerchantAdminView>> GetAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<MerchantAdminView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);
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

    public async Task<Result> ActivateAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var activateResult = merchant.Activate(timeProvider.GetUtcNow());
        if (activateResult.IsFailure)
            return activateResult;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> FreezeAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.Freeze(timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetSettlementDelayAsync(Guid merchantId, int days, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.SetSettlementDelay(days, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> SetSettlementWalletAsync(
        Guid merchantId, Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        var result = merchant.SetSettlementWallet(chain, address, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<MerchantRegistrationResult>> RotateCredentialAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<MerchantRegistrationResult>(MerchantErrors.NotFound);

        var now = timeProvider.GetUtcNow();

        // Revoke every currently-active credential — old raw secrets stop working immediately, the same
        // guarantee a password change gives, even though a merchant may (rarely) hold more than one.
        foreach (var active in merchant.Credentials.Where(c => c.IsActive).ToList())
        {
            var revokeResult = merchant.RevokeCredential(active.Id, now);
            if (revokeResult.IsFailure)
                return Result.Failure<MerchantRegistrationResult>(revokeResult.Error!);
        }

        var credential = generator.Generate();
        var secretHash = hasher.Hash(credential.Secret);
        var signingSecretCipher = secretCipher.Protect(credential.SigningSecret);

        var issueResult = merchant.IssueCredential(credential.ApiKey, secretHash, hasher.CurrentVersion, signingSecretCipher, now);
        if (issueResult.IsFailure)
            return Result.Failure<MerchantRegistrationResult>(issueResult.Error!);

        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantRegistrationResult(
            merchant.Id, merchant.MerchantCode, credential.ApiKey, credential.Secret, credential.SigningSecret));
    }

    public async Task<Result<AllowedIpsChange>> UpdateAllowedIpsAsync(
        Guid merchantId, IReadOnlyCollection<string> validIps, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<AllowedIpsChange>(MerchantErrors.NotFound);

        var result = merchant.UpdateAllowedIps(validIps, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<Result<MerchantAdminView>> GetAsync(Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        return merchant is null
            ? Result.Failure<MerchantAdminView>(MerchantErrors.NotFound)
            : Result.Success(ToView(merchant));
    }

    public async Task<(IReadOnlyList<MerchantAdminView> Items, int TotalCount)> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (items, total) = await repository.GetPagedAsync(page, pageSize, cancellationToken);
        return (items.Select(ToView).ToList(), total);
    }

    private static MerchantAdminView ToView(Domain.Merchant merchant) => new(
        merchant.Id,
        merchant.MerchantCode,
        merchant.Name,
        merchant.Status.ToString(),
        merchant.SettlementDelayDays,
        merchant.CreatedAt,
        merchant.Credentials.Any(c => c.IsActive),
        merchant.Configuration.AllowedIps,
        merchant.SettlementWallets.Select(w => new MerchantSettlementWalletView(w.Chain.ToString(), w.Address)).ToList());
}
