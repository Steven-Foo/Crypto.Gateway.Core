using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;

/// <summary>
/// Reads and updates the screening thresholds. Validation lives in
/// <see cref="ScreeningPolicyVersion.Create"/>, because those are the rules themselves rather than input
/// formatting, and a host, a seeder and a test must all be held to them equally.
/// </summary>
public sealed class ScreeningPolicyService(
    IScreeningPolicyRepository repository,
    ScreeningPolicyProvider provider,
    ScreeningPolicyCache cache,
    TimeProvider clock,
    ILogger<ScreeningPolicyService> logger) : IScreeningPolicyService
{
    public async Task<ScreeningPolicyView> GetAsync(CancellationToken cancellationToken = default) =>
        ToView(await provider.GetAsync(cancellationToken));

    public ScreeningPolicyView GetConfiguredDefaults() => ToView(provider.ConfiguredDefaults());

    public async Task<IReadOnlyList<ScreeningPolicyHistoryEntry>> GetHistoryAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        var versions = await repository.ListAsync(Math.Clamp(limit, 1, 200), cancellationToken);

        return [.. versions.Select(v => new ScreeningPolicyHistoryEntry(
            v.Id, v.BlockScore, v.ReviewScore, v.CacheDays, v.IndirectReviewMaxHops,
            v.IndirectReviewMinPercent, v.ExtraAlwaysBlockIndicators(), v.UpdatedBy, v.UpdatedAt, v.Note))];
    }

    public async Task<Result<ScreeningPolicyView>> UpdateAsync(
        ScreeningPolicyUpdate update, string updatedBy, CancellationToken cancellationToken = default)
    {
        var created = ScreeningPolicyVersion.Create(
            update.BlockScore, update.ReviewScore, update.CacheDays, update.IndirectReviewMaxHops,
            update.IndirectReviewMinPercent, update.AddedIndicators, update.Note, updatedBy,
            clock.GetUtcNow());

        if (created.IsFailure)
        {
            return Result.Failure<ScreeningPolicyView>(created.Error!);
        }

        await repository.AddAsync(created.Value, cancellationToken);

        // Drop the cached snapshot so this host reflects the change at once instead of waiting out its own
        // window. Other hosts pick it up when theirs expires — seconds, not a restart.
        cache.Invalidate();

        // A threshold change decides whether money moves, so it is worth a log line of its own rather than
        // living only in a table someone has to know to look at.
        logger.LogWarning(
            "Screening policy updated by {UpdatedBy}: block>={Block}, review>={Review}, cache={CacheDays}d, "
            + "proximity={Hops}hops/{Percent}pct. {Note}",
            updatedBy, update.BlockScore, update.ReviewScore, update.CacheDays,
            update.IndirectReviewMaxHops, update.IndirectReviewMinPercent, update.Note ?? "(no note)");

        return Result.Success(ToView(await provider.GetAsync(cancellationToken)));
    }

    private static ScreeningPolicyView ToView(ScreeningPolicy policy) => new(
        policy.BlockScore,
        policy.ReviewScore,
        policy.CacheDays,
        policy.IndirectReviewMaxHops,
        policy.IndirectReviewMinPercent,
        policy.AlwaysBlockIndicators,

        policy.EditableIndicators,
        policy.Source.ToString(),
        policy.UpdatedBy,
        policy.UpdatedAt,
        policy.Note);
}
