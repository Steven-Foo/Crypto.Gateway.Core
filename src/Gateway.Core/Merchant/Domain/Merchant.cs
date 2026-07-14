using System.Numerics;
using System.Text.RegularExpressions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// Aggregate root for an integrated partner. Owns its configuration, API credentials, and
/// per-asset policies. Merchants are never deleted — a closed merchant keeps its history so the
/// ledger stays explicable.
/// </summary>
public sealed partial class Merchant : Entity<Guid>
{
    public const int MinCodeLength = 3;
    public const int MaxCodeLength = 64;

    private readonly List<MerchantApiCredential> _credentials = [];
    private readonly List<MerchantAssetPolicy> _assetPolicies = [];

    private Merchant(Guid id, string merchantCode, string name, string? callbackUrl, DateTimeOffset createdAt)
        : base(id)
    {
        MerchantCode = merchantCode;
        Name = name;
        CallbackUrl = callbackUrl;
        Status = MerchantStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Configuration = MerchantConfiguration.CreateDefault(id, createdAt);
    }

    private Merchant() : base(Guid.Empty)
    {
    }

    public string MerchantCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? CallbackUrl { get; private set; }
    public MerchantStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public MerchantConfiguration Configuration { get; private set; } = null!;
    public IReadOnlyList<MerchantApiCredential> Credentials => _credentials;
    public IReadOnlyList<MerchantAssetPolicy> AssetPolicies => _assetPolicies;

    public bool CanTransact => Status == MerchantStatus.Active;

    public static Result<Merchant> Create(
        string merchantCode,
        string name,
        string? callbackUrl,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(merchantCode))
            return Result.Failure<Merchant>(MerchantErrors.CodeRequired);

        var normalisedCode = merchantCode.Trim().ToUpperInvariant();
        if (!MerchantCodePattern().IsMatch(normalisedCode))
            return Result.Failure<Merchant>(MerchantErrors.CodeInvalid);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Merchant>(MerchantErrors.NameRequired);

        var callbackResult = NormaliseCallbackUrl(callbackUrl);
        if (callbackResult.IsFailure)
            return Result.Failure<Merchant>(callbackResult.Error!);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return Result.Success(new Merchant(Guid.CreateVersion7(), normalisedCode, name.Trim(), callbackResult.Value, now));
    }

    public Result Activate(DateTimeOffset now) => TransitionTo(MerchantStatus.Active, now);

    public Result Suspend(DateTimeOffset now) => TransitionTo(MerchantStatus.Suspended, now);

    public Result Close(DateTimeOffset now) => TransitionTo(MerchantStatus.Closed, now);

    public Result UpdateCallbackUrl(string? callbackUrl, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var callbackResult = NormaliseCallbackUrl(callbackUrl);
        if (callbackResult.IsFailure)
            return callbackResult;

        CallbackUrl = callbackResult.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result UpdateConfiguration(bool autoSweepEnabled, int webhookRetryCount, bool isEnabled, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var result = Configuration.Update(autoSweepEnabled, webhookRetryCount, isEnabled, now);
        if (result.IsSuccess)
            UpdatedAt = now;

        return result;
    }

    /// <summary>
    /// The caller supplies an already-hashed secret — the aggregate never sees the plaintext, so it
    /// cannot accidentally persist or log it. Multiple active credentials are intentional: that is
    /// what makes zero-downtime key rotation possible.
    /// </summary>
    public Result<MerchantApiCredential> IssueCredential(
        string apiKey,
        string secretHash,
        int hashVersion,
        DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure<MerchantApiCredential>(MerchantErrors.Closed);

        var credential = MerchantApiCredential.Issue(Id, apiKey, secretHash, hashVersion, now);
        _credentials.Add(credential);
        UpdatedAt = now;
        return Result.Success(credential);
    }

    public Result RevokeCredential(Guid credentialId, DateTimeOffset now)
    {
        var credential = _credentials.SingleOrDefault(c => c.Id == credentialId);
        if (credential is null)
            return Result.Failure(MerchantErrors.CredentialNotFound);

        var result = credential.Revoke(now);
        if (result.IsSuccess)
            UpdatedAt = now;

        return result;
    }

    public Result SetAssetPolicy(
        Guid assetId,
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        BigInteger withdrawalFee,
        DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var existing = _assetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        if (existing is not null)
        {
            var updateResult = existing.Update(sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee, now);
            if (updateResult.IsSuccess)
                UpdatedAt = now;

            return updateResult;
        }

        var createResult = MerchantAssetPolicy.Create(
            Id, assetId, sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee, now);

        if (createResult.IsFailure)
            return Result.Failure(createResult.Error!);

        _assetPolicies.Add(createResult.Value);
        UpdatedAt = now;
        return Result.Success();
    }

    private Result TransitionTo(MerchantStatus target, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        Status = target;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result<string?> NormaliseCallbackUrl(string? callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            return Result.Success<string?>(null);

        var trimmed = callbackUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Result.Failure<string?>(MerchantErrors.CallbackUrlInvalid);
        }

        return Result.Success<string?>(uri.ToString());
    }

    /// <summary>Must stay in sync with <see cref="MinCodeLength"/>/<see cref="MaxCodeLength"/>;
    /// <c>GeneratedRegex</c> requires a literal, so it cannot interpolate them.</summary>
    [GeneratedRegex("^[A-Z0-9_-]{3,64}$")]
    private static partial Regex MerchantCodePattern();
}
