using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;

/// <summary>
/// The thresholds actually in force: what staff have set, over what configuration ships.
///
/// <para><b>Why both, rather than one or the other.</b> Configuration alone means every tuning change is a
/// deployment, which is untenable for numbers meant to be adjusted from measurement. The database alone
/// means a fresh environment starts with no policy at all, and "no policy" on a control that decides whether
/// money moves is the worst possible default. So configuration is the floor a system always has, and a
/// stored version overrides it once staff have deliberately set one.</para>
///
/// <para><b>Master switches are NOT here.</b> Whether screening runs at all stays in configuration, and is
/// read straight from <see cref="ComplianceOptions.Enabled"/>. These are calibration; that is existence. A
/// stolen session should not be able to switch off the control that holds money — that should require
/// infrastructure access.</para>
/// </summary>
/// <param name="BlockScore">Score at or above which an address is refused.</param>
/// <param name="ReviewScore">Score at or above which an address is held for a human.</param>
/// <param name="CacheDays">How long a completed verdict is reusable.</param>
/// <param name="IndirectReviewMaxHops">Proximity rule hop limit; zero disables it.</param>
/// <param name="IndirectReviewMinPercent">Proximity rule volume floor, 0-100.</param>
/// <param name="AlwaysBlockIndicators">The platform's designations plus anything staff added. Add-only: what
/// ships cannot be removed through the API, because a sanctions override is exactly the rule that should not
/// be deletable from a web form.</param>
/// <param name="Source">Where the values came from, so a read-back can say whether anyone has ever set them.</param>
public sealed record ScreeningPolicy(
    int BlockScore,
    int ReviewScore,
    int CacheDays,
    int IndirectReviewMaxHops,
    decimal IndirectReviewMinPercent,
    IReadOnlyList<string> AlwaysBlockIndicators,

    /// <summary>The subset staff added and may remove again. The rest ships with the platform. Carried
    /// here rather than derived by a caller, because only this type knows which came from where.</summary>
    IReadOnlyList<string> EditableIndicators,
    ScreeningPolicySource Source,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? Note = null)
{
    public bool ProximityRuleEnabled => IndirectReviewMaxHops > 0;

    /// <summary>Rendered onto every evidence row, so a decision can be re-read against the policy that was
    /// actually in force rather than against today's.</summary>
    public string Describe() =>
        $"block>={BlockScore};review>={ReviewScore};always_block_direct_only={string.Join(',', AlwaysBlockIndicators)}"
        + (ProximityRuleEnabled
            ? $";indirect_review<={IndirectReviewMaxHops}hops>={IndirectReviewMinPercent}pct"
            : string.Empty);
}

public enum ScreeningPolicySource
{
    /// <summary>Nobody has set a policy; the configured defaults are in force.</summary>
    Configuration = 0,

    /// <summary>A version staff saved is in force.</summary>
    Stored = 1
}

/// <summary>
/// Resolves the effective policy, cached briefly so a screening does not read the database per address.
///
/// <para>The cache window is what lets a change made on the ops host reach the money host's workers without
/// a restart. It is deliberately short: a stale threshold is only a problem for the few seconds after a
/// deliberate change, and the alternative — a per-screening database read on the hot path — buys nothing
/// that matters.</para>
/// </summary>
public sealed class ScreeningPolicyProvider(
    IScreeningPolicyRepository repository,
    IOptions<ComplianceOptions> options,
    ScreeningPolicyCache cache,
    TimeProvider clock)
{
    private readonly ComplianceOptions _options = options.Value;

    public async Task<ScreeningPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (cache.TryGet(now, out var cached))
        {
            return cached;
        }

        var stored = await repository.FindLatestAsync(cancellationToken);
        var policy = Merge(stored);
        cache.Set(policy, now);
        return policy;
    }

    /// <summary>Reads the configured defaults without consulting the database, for a read-back that wants to
    /// show what the system would fall back to.</summary>
    public ScreeningPolicy ConfiguredDefaults() => Merge(null);

    private ScreeningPolicy Merge(ScreeningPolicyVersion? stored)
    {
        // The shipped designations are always present. Staff add to them; the API cannot take them away.
        var indicators = _options.EffectiveAlwaysBlockIndicators.ToList();

        if (stored is null)
        {
            return new ScreeningPolicy(
                _options.BlockScore, _options.ReviewScore, _options.CacheDays,
                _options.IndirectReviewMaxHops, _options.IndirectReviewMinPercent,
                indicators, EditableIndicators: [], ScreeningPolicySource.Configuration);
        }

        var extras = stored.ExtraAlwaysBlockIndicators();
        indicators.AddRange(extras);

        return new ScreeningPolicy(
            stored.BlockScore, stored.ReviewScore, stored.CacheDays,
            stored.IndirectReviewMaxHops, stored.IndirectReviewMinPercent,
            [.. indicators.Distinct(StringComparer.OrdinalIgnoreCase)],
            extras,
            ScreeningPolicySource.Stored, stored.UpdatedBy, stored.UpdatedAt, stored.Note);
    }
}

/// <summary>
/// Holds the resolved policy for a short window. A singleton, because the provider that reads it is scoped
/// and a per-scope cache would expire on every request and cache nothing.
/// </summary>
public sealed class ScreeningPolicyCache
{
    private readonly Lock _gate = new();
    private ScreeningPolicy? _policy;
    private DateTimeOffset _expiresAt;

    /// <summary>How long a resolved policy is reused. Short enough that a deliberate change takes effect
    /// promptly across hosts, long enough that a burst of screenings is one database read.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public bool TryGet(DateTimeOffset now, out ScreeningPolicy policy)
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

    public void Set(ScreeningPolicy policy, DateTimeOffset now)
    {
        lock (_gate)
        {
            _policy = policy;
            _expiresAt = now + Window;
        }
    }

    /// <summary>Drops the cached value so the next read reloads. Called after a save, so the host that made
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
