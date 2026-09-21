using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;

/// <summary>
/// The thresholds in force, as staff see them.
/// </summary>
/// <param name="Source">
/// <c>Configuration</c> means nobody has ever saved a policy and the deployed defaults apply;
/// <c>Stored</c> means a saved version is in force. The distinction matters on a settings screen: "nobody
/// has set this" and "someone set it to exactly the default" look identical otherwise, and only one of them
/// is a question worth asking.
/// </param>
/// <param name="AlwaysBlockIndicators">Everything currently forcing a Block on a direct match — the
/// platform's own designations plus whatever staff added. See <paramref name="EditableIndicators"/> for the
/// part that can be changed.</param>
/// <param name="EditableIndicators">The subset staff added and may remove. The remainder ships with the
/// platform and is deliberately not removable through this API.</param>
public sealed record ScreeningPolicyView(
    int BlockScore,
    int ReviewScore,
    int CacheDays,
    int IndirectReviewMaxHops,
    decimal IndirectReviewMinPercent,
    IReadOnlyList<string> AlwaysBlockIndicators,
    IReadOnlyList<string> EditableIndicators,
    string Source,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    string? Note);

/// <summary>One past version, for the change history.</summary>
public sealed record ScreeningPolicyHistoryEntry(
    Guid Id,
    int BlockScore,
    int ReviewScore,
    int CacheDays,
    int IndirectReviewMaxHops,
    decimal IndirectReviewMinPercent,
    IReadOnlyList<string> AddedIndicators,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    string? Note);

/// <summary>What staff are saving. Every field is required: a partial update on a policy screen invites
/// changing one threshold and silently reverting another to a stale value the caller happened to hold.</summary>
public sealed record ScreeningPolicyUpdate(
    int BlockScore,
    int ReviewScore,
    int CacheDays,
    int IndirectReviewMaxHops,
    decimal IndirectReviewMinPercent,
    IReadOnlyList<string>? AddedIndicators,
    string? Note);

/// <summary>
/// Reads and updates the screening thresholds (§4.5 — a host consumes this and never touches the module's
/// persistence).
///
/// <para><b>Only calibration is here.</b> Whether screening runs at all, whether payouts are gated, whether
/// settlement wallets are checked — those stay in configuration by design. They decide whether a control
/// exists; these decide how it is tuned. A stolen session must not be able to switch off the control that
/// holds money, and keeping that in configuration means it takes infrastructure access rather than a
/// browser tab.</para>
///
/// <para>Every update appends a version rather than overwriting one, so the trail of who calibrated the
/// control and when survives, and an old evidence row stays explainable against the policy it was actually
/// judged under.</para>
/// </summary>
public interface IScreeningPolicyService
{
    Task<ScreeningPolicyView> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured defaults, regardless of what is stored — what the system falls back to if no
    /// version had ever been saved. Shown beside the current values so staff can see what they have
    /// changed.</summary>
    ScreeningPolicyView GetConfiguredDefaults();

    Task<IReadOnlyList<ScreeningPolicyHistoryEntry>> GetHistoryAsync(
        int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new version. <paramref name="updatedBy"/> is the acting staff member and is required — a
    /// threshold change is a compliance act and an unattributable one is not worth recording.
    /// </summary>
    Task<Result<ScreeningPolicyView>> UpdateAsync(
        ScreeningPolicyUpdate update, string updatedBy, CancellationToken cancellationToken = default);
}
