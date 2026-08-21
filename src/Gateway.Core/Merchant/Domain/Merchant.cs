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

    /// <summary>Upper bound on the settlement delay (T+N). Deposits mature into withdrawable funds at most
    /// this many days after confirmation; a larger value is almost certainly a misconfiguration.</summary>
    public const int MaxSettlementDelayDays = 30;

    private readonly List<MerchantApiCredential> _credentials = [];
    private readonly List<MerchantAssetPolicy> _assetPolicies = [];
    private readonly List<MerchantSettlementWallet> _settlementWallets = [];

    private Merchant(Guid id, string merchantCode, string name, string? callbackUrl, DateTimeOffset createdAt)
        : base(id)
    {
        MerchantCode = merchantCode;
        Name = name;
        CallbackUrl = callbackUrl;
        Status = MerchantStatus.Active;
        SettlementDelayDays = 0;
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

    /// <summary>The merchant's settlement period, in whole days (T+N). Deposits confirmed on calendar day D
    /// (UTC) become withdrawable at 00:00 UTC of day D+N. 0 = T+0 (immediately withdrawable). Gates BOTH the
    /// merchant's earnings cash-out and the user payouts it sends — only settled funds may leave. The
    /// maturity math lives in the Ledger's settled-balance query; this is just the admin-set policy value.</summary>
    public int SettlementDelayDays { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public MerchantConfiguration Configuration { get; private set; } = null!;
    public IReadOnlyList<MerchantApiCredential> Credentials => _credentials;
    public IReadOnlyList<MerchantAssetPolicy> AssetPolicies => _assetPolicies;
    public IReadOnlyList<MerchantSettlementWallet> SettlementWallets => _settlementWallets;

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

    /// <summary>Also reopens a <c>Closed</c> merchant — status transitions are always reversible (see
    /// <see cref="TransitionTo"/>); a "closed" merchant is a reversible administrative state, not a
    /// deleted/terminal one.</summary>
    public Result Activate(DateTimeOffset now) => TransitionTo(MerchantStatus.Active, now);

    /// <summary>Admin risk-hold — blocks all transacting via <c>CanTransact</c>. Reversible: <see cref="Activate"/>
    /// unfreezes. Does not stop crediting funds already sent on-chain (§14) — it stops new activity. Also
    /// reachable from <c>Closed</c> (reopens into a frozen, not active, state).</summary>
    public Result Freeze(DateTimeOffset now) => TransitionTo(MerchantStatus.Frozen, now);

    /// <summary>Admin action — blocks all transacting via <c>CanTransact</c>, same as <see cref="Freeze"/>.
    /// Reversible: <see cref="Activate"/>/<see cref="Freeze"/> can move a Closed merchant back out (§ status
    /// transitions are never terminal — only the per-operation guards on other business methods, e.g.
    /// <see cref="IssueCredential"/>, independently keep rejecting while Closed).</summary>
    public Result Close(DateTimeOffset now) => TransitionTo(MerchantStatus.Closed, now);

    /// <summary>Sets the settlement period (T+N) in whole days. 0 = T+0. Rejects a negative value or one
    /// beyond <see cref="MaxSettlementDelayDays"/> (near-certain misconfiguration).</summary>
    public Result SetSettlementDelay(int days, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        if (days < 0 || days > MaxSettlementDelayDays)
            return Result.Failure(MerchantErrors.SettlementDelayInvalid);

        SettlementDelayDays = days;
        UpdatedAt = now;
        return Result.Success();
    }

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

    /// <summary>Replaces this merchant's IP allowlist; see <see cref="MerchantConfiguration.UpdateAllowedIps"/>.</summary>
    public Result<AllowedIpsChange> UpdateAllowedIps(IReadOnlyCollection<string> validIps, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure<AllowedIpsChange>(MerchantErrors.Closed);

        var change = Configuration.UpdateAllowedIps(validIps, now);
        UpdatedAt = now;
        return Result.Success(change);
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
        string signingSecretCipher,
        DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure<MerchantApiCredential>(MerchantErrors.Closed);

        var credential = MerchantApiCredential.Issue(Id, apiKey, secretHash, hashVersion, signingSecretCipher, now);
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
        BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        FeeSchedule fees,
        DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var existing = _assetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        if (existing is not null)
        {
            var updateResult = existing.Update(sweepThreshold, minimumWithdrawal, maximumWithdrawal, fees, now);
            if (updateResult.IsSuccess)
                UpdatedAt = now;

            return updateResult;
        }

        var createResult = MerchantAssetPolicy.Create(
            Id, assetId, sweepThreshold, minimumWithdrawal, maximumWithdrawal, fees, now);

        if (createResult.IsFailure)
            return Result.Failure(createResult.Error!);

        _assetPolicies.Add(createResult.Value);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Registers or updates the merchant's settlement (cash-out) wallet for a chain — the fixed
    /// destination of a Merchant Withdrawal. One per chain; re-registering updates the address.</summary>
    public Result SetSettlementWallet(Chain chain, string address, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var existing = _settlementWallets.SingleOrDefault(w => w.Chain == chain);
        if (existing is not null)
        {
            var updateResult = existing.Update(address, now);
            if (updateResult.IsSuccess)
                UpdatedAt = now;

            return updateResult;
        }

        var createResult = MerchantSettlementWallet.Create(Id, chain, address, now);
        if (createResult.IsFailure)
            return Result.Failure(createResult.Error!);

        _settlementWallets.Add(createResult.Value);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Sets the per-merchant user-withdrawal min/max override for an asset. Null = unset (the flow uses
    /// the platform config default). Creates an unpriced policy (limits only) if none exists, otherwise updates
    /// just the limits — fees and the cash-out cap are preserved.</summary>
    public Result SetWithdrawalLimits(Guid assetId, BigInteger? minimum, BigInteger? maximum, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var policy = _assetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        if (policy is null)
        {
            var createResult = MerchantAssetPolicy.Create(
                Id, assetId, BigInteger.Zero, null, null, FeeSchedule.None, now);
            if (createResult.IsFailure)
                return Result.Failure(createResult.Error!);

            policy = createResult.Value;
            _assetPolicies.Add(policy);
        }

        var result = policy.SetWithdrawalLimits(minimum, maximum, now);
        if (result.IsSuccess)
            UpdatedAt = now;

        return result;
    }

    /// <summary>Sets the merchant-withdrawal (cash-out) liquidity cap for an asset. Creates an unpriced policy
    /// (cap only) if none exists, otherwise updates just the cap — fees and user limits are preserved.</summary>
    public Result SetMerchantWithdrawalCap(Guid assetId, BigInteger? flatCap, int percentBps, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var policy = _assetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        if (policy is null)
        {
            var createResult = MerchantAssetPolicy.Create(
                Id, assetId, BigInteger.Zero, null, null, FeeSchedule.None, now);
            if (createResult.IsFailure)
                return Result.Failure(createResult.Error!);

            policy = createResult.Value;
            _assetPolicies.Add(policy);
        }

        var capResult = policy.SetMerchantWithdrawalCap(flatCap, percentBps, now);
        if (capResult.IsSuccess)
            UpdatedAt = now;

        return capResult;
    }

    /// <summary>Sets the per-merchant approval-threshold override for an asset — the payout amount above which
    /// a withdrawal (user payout OR cash-out) needs human oversight. Null = unset (the flow uses the platform
    /// config threshold). Creates an unpriced policy (threshold only) if none exists, otherwise updates just the
    /// threshold — fees, user limits, and the cash-out cap are preserved.</summary>
    public Result SetApprovalThreshold(Guid assetId, BigInteger? approvalThreshold, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
            return Result.Failure(MerchantErrors.Closed);

        var policy = _assetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        if (policy is null)
        {
            var createResult = MerchantAssetPolicy.Create(
                Id, assetId, BigInteger.Zero, null, null, FeeSchedule.None, now);
            if (createResult.IsFailure)
                return Result.Failure(createResult.Error!);

            policy = createResult.Value;
            _assetPolicies.Add(policy);
        }

        var result = policy.SetApprovalThreshold(approvalThreshold, now);
        if (result.IsSuccess)
            UpdatedAt = now;

        return result;
    }

    /// <summary>Status itself is always reversible — Active/Frozen/Closed can move freely between each other
    /// via <see cref="Activate"/>/<see cref="Freeze"/>/<see cref="Close"/> (e.g. a closed merchant can be
    /// reopened). "Closed blocks everything else" is enforced per-operation instead, by the individual guards
    /// on every other business method (<see cref="UpdateCallbackUrl"/>, <see cref="IssueCredential"/>, etc.) —
    /// those still reject while Closed, deliberately independent of this transition.</summary>
    private Result TransitionTo(MerchantStatus target, DateTimeOffset now)
    {
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
