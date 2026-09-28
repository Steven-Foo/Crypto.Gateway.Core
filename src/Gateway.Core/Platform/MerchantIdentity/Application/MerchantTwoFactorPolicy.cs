using System.Collections.Concurrent;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

/// <summary>Configuration floor for the platform minimum.</summary>
public sealed class MerchantTwoFactorPolicyOptions
{
    public const string SectionName = "MerchantIdentity:TwoFactorPolicy";

    /// <summary>
    /// The platform minimum until staff save one. <b>Empty ⇒ the recommended baseline</b>
    /// (<see cref="MerchantGuardedActions.RecommendedCodes"/>), never "nothing": a fresh environment must start
    /// protected. List codes only to override that baseline for a deployment. Deliberately empty in code —
    /// .NET appends a configured array to the code default, which is how duplicated lists happen.
    /// </summary>
    public IList<string> PlatformGuardedActions { get; set; } = [];
}

public enum MerchantTwoFactorPolicySource
{
    /// <summary>Nobody has saved this layer — the default is in force.</summary>
    Default = 0,

    /// <summary>Someone saved it.</summary>
    Stored = 1,
}

/// <summary>The platform minimum as currently in force.</summary>
public sealed record MerchantPlatformTwoFactorPolicy(
    IReadOnlyList<string> GuardedActions,
    MerchantTwoFactorPolicySource Source,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? Note = null);

/// <summary>
/// The effective 2FA action list for ONE merchant: the platform minimum ∪ the merchant's own additions ∪ the
/// always-on self-protecting action.
/// </summary>
/// <param name="GuardedActions">Everything that demands a code for this merchant. Sorted.</param>
/// <param name="PlatformRequired">The platform minimum — shown locked on the merchant's page.</param>
/// <param name="MerchantAdded">What this merchant added on top (never overlaps <paramref name="PlatformRequired"/>).</param>
public sealed record MerchantTwoFactorPolicy(
    IReadOnlyList<string> GuardedActions,
    IReadOnlyList<string> PlatformRequired,
    IReadOnlyList<string> MerchantAdded,
    MerchantTwoFactorPolicySource MerchantSource,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? Note = null)
{
    public bool IsGuarded(string action) => GuardedActions.Contains(action, StringComparer.Ordinal);
}

/// <summary>
/// Resolves the platform minimum and each merchant's effective list, cached briefly. The window is what lets a
/// change saved on one host (the admin back office saves the minimum; the portal enforces it) reach the other
/// without a restart, while sparing a database read on every guarded request.
/// </summary>
public sealed class MerchantTwoFactorPolicyProvider(
    IMerchantTwoFactorPolicyRepository repository,
    IOptions<MerchantTwoFactorPolicyOptions> options,
    MerchantTwoFactorPolicyCache cache,
    TimeProvider clock)
{
    public async Task<MerchantPlatformTwoFactorPolicy> GetPlatformAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (cache.TryGetPlatform(now, out var cached))
            return cached;

        var stored = await repository.FindLatestPlatformAsync(cancellationToken);
        var policy = stored is null
            ? new MerchantPlatformTwoFactorPolicy(ConfiguredPlatformDefault(), MerchantTwoFactorPolicySource.Default)
            : new MerchantPlatformTwoFactorPolicy(
                KnownOnly(stored.GuardedActions()), MerchantTwoFactorPolicySource.Stored,
                stored.UpdatedBy, stored.UpdatedAt, stored.Note);

        cache.SetPlatform(policy, now);
        return policy;
    }

    public async Task<MerchantTwoFactorPolicy> GetForMerchantAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (cache.TryGetMerchant(merchantId, now, out var cached))
            return cached;

        var platform = await GetPlatformAsync(cancellationToken);
        var stored = await repository.FindLatestForMerchantAsync(merchantId, cancellationToken);

        // Additions exclude anything the platform now requires: if the platform later adds an action a merchant
        // had already added, it simply moves to the locked column rather than appearing twice.
        var added = stored is null
            ? []
            : KnownOnly(stored.GuardedActions()).Except(platform.GuardedActions, StringComparer.Ordinal).ToList();

        var effective = platform.GuardedActions
            .Concat(added)
            .Append(MerchantGuardedActions.TwoFactorPolicy)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var policy = new MerchantTwoFactorPolicy(
            effective, platform.GuardedActions, added,
            stored is null ? MerchantTwoFactorPolicySource.Default : MerchantTwoFactorPolicySource.Stored,
            stored?.UpdatedBy, stored?.UpdatedAt, stored?.Note);

        cache.SetMerchant(merchantId, policy, now);
        return policy;
    }

    /// <summary>What the platform minimum is when staff have saved nothing: configuration if it names any
    /// actions, else the recommended baseline.</summary>
    public IReadOnlyList<string> ConfiguredPlatformDefault()
    {
        var configured = KnownOnly(options.Value.PlatformGuardedActions);
        return configured.Count > 0 ? configured : KnownOnly(MerchantGuardedActions.RecommendedCodes);
    }

    /// <summary>Drops codes no longer in the catalog (a removed action must not linger as a phantom toggle),
    /// trims, de-duplicates, sorts.</summary>
    private static IReadOnlyList<string> KnownOnly(IEnumerable<string> codes) =>
        [.. codes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Where(MerchantGuardedActions.IsGuardable)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}

/// <summary>
/// Singleton cache for the provider (the provider is scoped; a per-scope cache would cache nothing).
/// </summary>
public sealed class MerchantTwoFactorPolicyCache
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private MerchantPlatformTwoFactorPolicy? _platform;
    private DateTimeOffset _platformExpiresAt;
    private readonly ConcurrentDictionary<Guid, (MerchantTwoFactorPolicy Policy, DateTimeOffset ExpiresAt)> _merchants = new();

    public bool TryGetPlatform(DateTimeOffset now, out MerchantPlatformTwoFactorPolicy policy)
    {
        lock (_gate)
        {
            if (_platform is not null && now < _platformExpiresAt)
            {
                policy = _platform;
                return true;
            }
        }

        policy = null!;
        return false;
    }

    public void SetPlatform(MerchantPlatformTwoFactorPolicy policy, DateTimeOffset now)
    {
        lock (_gate)
        {
            _platform = policy;
            _platformExpiresAt = now + Window;
        }
    }

    public bool TryGetMerchant(Guid merchantId, DateTimeOffset now, out MerchantTwoFactorPolicy policy)
    {
        if (_merchants.TryGetValue(merchantId, out var entry) && now < entry.ExpiresAt)
        {
            policy = entry.Policy;
            return true;
        }

        policy = null!;
        return false;
    }

    public void SetMerchant(Guid merchantId, MerchantTwoFactorPolicy policy, DateTimeOffset now) =>
        _merchants[merchantId] = (policy, now + Window);

    /// <summary>After a platform save every merchant's effective list may have changed, so all are dropped.</summary>
    public void InvalidatePlatform()
    {
        lock (_gate)
        {
            _platform = null;
            _platformExpiresAt = default;
        }

        _merchants.Clear();
    }

    public void InvalidateMerchant(Guid merchantId) => _merchants.TryRemove(merchantId, out _);
}

/// <summary>The platform minimum plus the context the admin settings screen needs.</summary>
public sealed record MerchantPlatformTwoFactorPolicyView(
    MerchantPlatformTwoFactorPolicy Current,
    IReadOnlyList<string> ConfiguredDefaults,
    IReadOnlyList<string> RecommendedDefaults);

public interface IMerchantTwoFactorPolicyService
{
    // ── the platform minimum (admin back office) ──
    Task<MerchantPlatformTwoFactorPolicyView> GetPlatformAsync(CancellationToken cancellationToken = default);

    Task<Result<MerchantPlatformTwoFactorPolicy>> SavePlatformAsync(
        IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the recommended baseline as the platform minimum.</summary>
    Task<Result<MerchantPlatformTwoFactorPolicy>> RestorePlatformDefaultsAsync(
        string? note, string updatedBy, CancellationToken cancellationToken = default);

    // ── one merchant (merchant portal, or read-only from the admin back office) ──
    Task<MerchantTwoFactorPolicy> GetForMerchantAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves what the merchant ticked. <paramref name="guardedActions"/> may be the FULL list shown on the page
    /// (locked platform rows included) — only the part the platform does not already require is stored, so a
    /// merchant can add but never remove a platform-required action.
    /// </summary>
    Task<Result<MerchantTwoFactorPolicy>> SaveForMerchantAsync(
        Guid merchantId, IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the merchant's own additions, back to exactly the platform minimum.</summary>
    Task<Result<MerchantTwoFactorPolicy>> RestoreMerchantDefaultsAsync(
        Guid merchantId, string? note, string updatedBy, CancellationToken cancellationToken = default);

    /// <summary><paramref name="merchantId"/> null ⇒ platform-minimum history; otherwise that merchant's own.</summary>
    Task<IReadOnlyList<MerchantTwoFactorPolicyVersion>> GetHistoryAsync(
        Guid? merchantId, int limit = 50, CancellationToken cancellationToken = default);
}

public sealed class MerchantTwoFactorPolicyService(
    IMerchantTwoFactorPolicyRepository repository,
    MerchantTwoFactorPolicyProvider provider,
    MerchantTwoFactorPolicyCache cache,
    TimeProvider clock) : IMerchantTwoFactorPolicyService
{
    public async Task<MerchantPlatformTwoFactorPolicyView> GetPlatformAsync(CancellationToken cancellationToken = default) =>
        new(await provider.GetPlatformAsync(cancellationToken),
            provider.ConfiguredPlatformDefault(),
            [.. MerchantGuardedActions.RecommendedCodes.Order(StringComparer.Ordinal)]);

    public async Task<Result<MerchantPlatformTwoFactorPolicy>> SavePlatformAsync(
        IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default)
    {
        if (!AllKnown(guardedActions))
            return Result.Failure<MerchantPlatformTwoFactorPolicy>(MerchantTwoFactorPolicyErrors.UnknownAction);

        var version = MerchantTwoFactorPolicyVersion.Create(null, guardedActions, note, updatedBy, clock.GetUtcNow());
        if (version.IsFailure)
            return Result.Failure<MerchantPlatformTwoFactorPolicy>(version.Error!);

        repository.Add(version.Value);
        await repository.SaveChangesAsync(cancellationToken);

        // After the commit, so a concurrent read can't repopulate the cache from the pre-save state.
        cache.InvalidatePlatform();
        return Result.Success(await provider.GetPlatformAsync(cancellationToken));
    }

    public Task<Result<MerchantPlatformTwoFactorPolicy>> RestorePlatformDefaultsAsync(
        string? note, string updatedBy, CancellationToken cancellationToken = default) =>
        SavePlatformAsync(
            MerchantGuardedActions.RecommendedCodes,
            string.IsNullOrWhiteSpace(note) ? "Restored the recommended security baseline." : note,
            updatedBy, cancellationToken);

    public Task<MerchantTwoFactorPolicy> GetForMerchantAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        provider.GetForMerchantAsync(merchantId, cancellationToken);

    public async Task<Result<MerchantTwoFactorPolicy>> SaveForMerchantAsync(
        Guid merchantId, IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default)
    {
        // The always-on action may be echoed back by a page that sends the full list; it is not "unknown".
        var submitted = guardedActions
            .Where(a => !string.Equals(a?.Trim(), MerchantGuardedActions.TwoFactorPolicy, StringComparison.Ordinal))
            .ToList();

        if (!AllKnown(submitted))
            return Result.Failure<MerchantTwoFactorPolicy>(MerchantTwoFactorPolicyErrors.UnknownAction);

        var platform = await provider.GetPlatformAsync(cancellationToken);
        var additions = submitted
            .Select(a => a.Trim())
            .Except(platform.GuardedActions, StringComparer.Ordinal)
            .ToList();

        var version = MerchantTwoFactorPolicyVersion.Create(merchantId, additions, note, updatedBy, clock.GetUtcNow());
        if (version.IsFailure)
            return Result.Failure<MerchantTwoFactorPolicy>(version.Error!);

        repository.Add(version.Value);
        await repository.SaveChangesAsync(cancellationToken);

        cache.InvalidateMerchant(merchantId);
        return Result.Success(await provider.GetForMerchantAsync(merchantId, cancellationToken));
    }

    public Task<Result<MerchantTwoFactorPolicy>> RestoreMerchantDefaultsAsync(
        Guid merchantId, string? note, string updatedBy, CancellationToken cancellationToken = default) =>
        SaveForMerchantAsync(
            merchantId, [],
            string.IsNullOrWhiteSpace(note) ? "Restored the platform default (removed all own additions)." : note,
            updatedBy, cancellationToken);

    public Task<IReadOnlyList<MerchantTwoFactorPolicyVersion>> GetHistoryAsync(
        Guid? merchantId, int limit = 50, CancellationToken cancellationToken = default) =>
        repository.ListHistoryAsync(merchantId, limit, cancellationToken);

    private static bool AllKnown(IEnumerable<string> codes) =>
        codes.All(c => !string.IsNullOrWhiteSpace(c) && MerchantGuardedActions.IsGuardable(c.Trim()));
}
