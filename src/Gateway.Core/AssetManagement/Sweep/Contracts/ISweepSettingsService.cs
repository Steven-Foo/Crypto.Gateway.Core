using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;

/// <summary>
/// One chain's sweep configuration as staff see it. Amounts are exact base-unit strings (§14) — the host
/// converts for display at the edge and keeps the integer for audit.
/// </summary>
/// <param name="Source"><c>Configuration</c> while the values still track the deployed defaults;
/// <c>Stored</c> once staff have saved them. "Nobody has set this" and "someone set it to exactly the
/// default" are otherwise indistinguishable, and only one of them is a question worth asking.</param>
/// <param name="ScanRequestedAt">Set when a manual run has been asked for and not yet picked up. The
/// back-office host runs no sweep workers, so a manual trigger is a request the money host honours on its
/// next look, not a scan performed in the request.</param>
public sealed record SweepSettingsView(
    string Chain,
    bool Enabled,
    string MinSweepAmountBaseUnits,
    int Confirmations,
    int ScanIntervalMinutes,
    string Source,
    DateTimeOffset? ScanRequestedAt,
    DateTimeOffset? LastScanStartedAt,
    DateTimeOffset? LastScanCompletedAt,
    int? LastSweepsCreated,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>
/// What staff are saving. Every field is required: a partial update on a settings screen invites changing
/// one dial and silently reverting another to whatever stale value the caller happened to hold.
/// </summary>
public sealed record SweepSettingsUpdate(
    bool Enabled,
    string MinSweepAmountBaseUnits,
    int Confirmations,
    int ScanIntervalMinutes);

/// <summary>
/// Reads and updates the sweep dials, and asks for an out-of-schedule pass (§4.5 — a host consumes this and
/// never touches the module's persistence).
/// </summary>
public interface ISweepSettingsService
{
    /// <summary>Settings for every chain that has a sweep policy, so a screen can show a chain that is
    /// configured but paused rather than omitting it.</summary>
    Task<IReadOnlyList<SweepSettingsView>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<SweepSettingsView>> GetAsync(Chain chain, CancellationToken cancellationToken = default);

    /// <summary><paramref name="updatedBy"/> is the acting staff member, taken from the validated session
    /// and never from the request body — an attribution the caller supplied is not an attribution.</summary>
    Task<Result<SweepSettingsView>> UpdateAsync(
        Chain chain, SweepSettingsUpdate update, string updatedBy, CancellationToken cancellationToken = default);

    /// <summary>Requests a scan on the next worker tick. Refused for a paused chain: "paused" has to mean
    /// paused, or the switch is not a switch.</summary>
    Task<Result<SweepSettingsView>> RequestScanAsync(
        Chain chain, string requestedBy, CancellationToken cancellationToken = default);
}
