using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

/// <summary>Where the policy in force came from. <see cref="Configuration"/> and <see cref="Stored"/> are
/// distinguished because "nobody has chosen yet" and "someone chose exactly the default" look identical in
/// the values alone, and only one of them is a question worth asking an operator.</summary>
public enum TwoFactorPolicySource
{
    Configuration = 0,
    Stored = 1,
}

/// <summary>
/// The effective policy: which action codes demand a second factor right now.
/// </summary>
/// <param name="GuardedActions">Action codes requiring a code. Always includes
/// <see cref="TwoFactorPolicyVersion.SelfProtectingAction"/>.</param>
public sealed record TwoFactorPolicy(
    IReadOnlyList<string> GuardedActions,
    TwoFactorPolicySource Source,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? Note = null)
{
    public bool IsGuarded(string action) =>
        GuardedActions.Contains(action, StringComparer.Ordinal);
}

/// <summary>Configuration floor for the policy. A fresh environment must boot with a defined posture rather
/// than with the control undefined, which is what a database-only policy would give it.</summary>
public sealed class TwoFactorOptions
{
    public const string SectionName = "Identity:TwoFactor";

    /// <summary>
    /// Actions guarded until staff save a version of their own.
    ///
    /// <para><b>Beware: .NET binds a configuration array by ADDING to the code default</b> — the trap that
    /// once doubled a screening bill (see <c>docs/address-screening.md</c> §12). The default here is
    /// deliberately EMPTY so there is nothing to append to, and the effective list is de-duplicated at the
    /// point of use regardless.</para>
    /// </summary>
    public IList<string> GuardedActions { get; set; } = [];

    /// <summary>Label shown in the authenticator app. Changing it after people have enrolled only renames
    /// new entries; existing ones keep whatever they were scanned with.</summary>
    public string Issuer { get; set; } = "CryptoPaymentEngine";
}

/// <summary>
/// Resolves the effective policy, cached briefly so the guarded-action filter does not read the database on
/// every request it gates.
///
/// <para>The window is what lets a change made on the settings screen reach every other request without a
/// restart. Deliberately short: a stale list is only a problem for the few seconds after a deliberate
/// change, and the alternative — a database read per gated request — buys nothing that matters.</para>
/// </summary>
public sealed class TwoFactorPolicyProvider(
    ITwoFactorPolicyRepository repository,
    IOptions<TwoFactorOptions> options,
    TwoFactorPolicyCache cache,
    TimeProvider clock)
{
    private readonly TwoFactorOptions _options = options.Value;

    public async Task<TwoFactorPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (cache.TryGet(now, out var cached))
            return cached;

        var stored = await repository.FindLatestAsync(cancellationToken);
        var policy = Merge(stored);
        cache.Set(policy, now);
        return policy;
    }

    /// <summary>The configured floor, without consulting the database — for a read-back that shows what the
    /// system would fall back to if the stored version were removed.</summary>
    public TwoFactorPolicy ConfiguredDefaults() => Merge(null);

    private TwoFactorPolicy Merge(TwoFactorPolicyVersion? stored)
    {
        IEnumerable<string> actions = stored is null ? _options.GuardedActions : stored.GuardedActions();

        // The self-protecting action is forced in on the read path as well as the write path. A version saved
        // before this action existed, or a configuration file that omits it, must not leave the policy screen
        // unguarded — which is precisely the row an attacker would go for first.
        var effective = actions
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Append(TwoFactorPolicyVersion.SelfProtectingAction)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return stored is null
            ? new TwoFactorPolicy(effective, TwoFactorPolicySource.Configuration)
            : new TwoFactorPolicy(
                effective, TwoFactorPolicySource.Stored, stored.UpdatedBy, stored.UpdatedAt, stored.Note);
    }
}

/// <summary>
/// Holds the resolved policy for a short window. A singleton, because the provider that reads it is scoped
/// and a per-scope cache would expire every request and cache nothing.
/// </summary>
public sealed class TwoFactorPolicyCache
{
    private readonly Lock _gate = new();
    private TwoFactorPolicy? _policy;
    private DateTimeOffset _expiresAt;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public bool TryGet(DateTimeOffset now, out TwoFactorPolicy policy)
    {
        lock (_gate)
        {
            if (_policy is not null && now < _expiresAt)
            {
                policy = _policy;
                return true;
            }
        }

        policy = null!;
        return false;
    }

    public void Set(TwoFactorPolicy policy, DateTimeOffset now)
    {
        lock (_gate)
        {
            _policy = policy;
            _expiresAt = now + Window;
        }
    }

    /// <summary>Drops the cached value so the next read reloads — called after a save, so the host that made
    /// the change reflects it immediately rather than waiting out its own window.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _policy = null;
            _expiresAt = default;
        }
    }
}

/// <summary>A saved policy plus the context a settings screen needs around it.</summary>
public sealed record TwoFactorPolicyView(
    TwoFactorPolicy Current,
    TwoFactorPolicy ConfiguredDefaults,
    int EnrolledStaffCount);

public interface ITwoFactorPolicyService
{
    Task<TwoFactorPolicyView> GetAsync(CancellationToken cancellationToken = default);

    Task<Result<TwoFactorPolicy>> SaveAsync(
        IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwoFactorPolicyVersion>> GetHistoryAsync(
        int limit = 50, CancellationToken cancellationToken = default);
}

public sealed class TwoFactorPolicyService(
    ITwoFactorPolicyRepository repository,
    IStaffTwoFactorRepository twoFactorRepository,
    TwoFactorPolicyProvider provider,
    TwoFactorPolicyCache cache,
    TimeProvider clock) : ITwoFactorPolicyService
{
    public async Task<TwoFactorPolicyView> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = await provider.GetAsync(cancellationToken);
        var enrolled = await twoFactorRepository.ListEnrolledStaffUserIdsAsync(cancellationToken);

        return new TwoFactorPolicyView(current, provider.ConfiguredDefaults(), enrolled.Count);
    }

    public async Task<Result<TwoFactorPolicy>> SaveAsync(
        IReadOnlyCollection<string> guardedActions, string? note, string updatedBy,
        CancellationToken cancellationToken = default)
    {
        var version = TwoFactorPolicyVersion.Create(guardedActions, note, updatedBy, clock.GetUtcNow());
        if (version.IsFailure)
            return Result.Failure<TwoFactorPolicy>(version.Error!);

        repository.Add(version.Value);
        await repository.SaveChangesAsync(cancellationToken);

        // Invalidate AFTER the write commits, so a concurrent read cannot repopulate the cache from the
        // pre-save state and then hold it for the full window.
        cache.Invalidate();

        return Result.Success(await provider.GetAsync(cancellationToken));
    }

    public Task<IReadOnlyList<TwoFactorPolicyVersion>> GetHistoryAsync(
        int limit = 50, CancellationToken cancellationToken = default) =>
        repository.ListHistoryAsync(limit, cancellationToken);
}
