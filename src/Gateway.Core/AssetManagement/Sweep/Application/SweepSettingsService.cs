using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>
/// The effective sweep dials for each chain: the deployed configuration as the floor, a stored row as the
/// operator's override, and the schedule state (when the last pass ran, whether a manual one was asked for).
///
/// <para>It serves both the workers, through <see cref="ISweepPolicyProvider"/>, and the back office,
/// through <see cref="ISweepSettingsService"/>, on purpose: a settings screen that read different values
/// from the ones the scan actually uses would be worse than no screen.</para>
///
/// <para>Scoped, and memoised for the life of the scope. A worker pass is one scope, so a pass reads each
/// chain's row once — fresh at the start of every pass, which is what makes a saved change take effect
/// without a restart.</para>
/// </summary>
public sealed class SweepSettingsService(
    ISweepSettingsRepository repository,
    ISweepConfigurationDefaults defaults,
    TimeProvider timeProvider,
    ILogger<SweepSettingsService> logger) : ISweepPolicyProvider, ISweepSettingsService
{
    private readonly Dictionary<Chain, SweepSettings> _loaded = [];

    public async Task<SweepPolicy> ForAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No sweep policy configured for {chain}. Add 'Sweep:Policies:{chain}'.");

        return new SweepPolicy(settings.MinSweepAmount, settings.Confirmations);
    }

    public async Task<IReadOnlyList<SweepSettingsView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var views = new List<SweepSettingsView>();

        // Driven by what is configured, not by what happens to be stored: a chain with a policy but no row
        // yet is still a chain an operator can pause or re-tune, and omitting it would hide it.
        foreach (var chain in defaults.Configured.Keys.OrderBy(c => c.ToString(), StringComparer.Ordinal))
        {
            var settings = await ResolveAsync(chain, cancellationToken);
            if (settings is not null)
                views.Add(Project(settings));
        }

        return views;
    }

    public async Task<Result<SweepSettingsView>> GetAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken);
        return settings is null
            ? Result.Failure<SweepSettingsView>(SweepErrors.ChainNotConfigured)
            : Result.Success(Project(settings));
    }

    public async Task<Result<SweepSettingsView>> UpdateAsync(
        Chain chain, SweepSettingsUpdate update, string updatedBy, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken);
        if (settings is null)
            return Result.Failure<SweepSettingsView>(SweepErrors.ChainNotConfigured);

        if (!BigInteger.TryParse(
                update.MinSweepAmountBaseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minSweep))
            return Result.Failure<SweepSettingsView>(SweepErrors.ThresholdNegative);

        var applied = settings.Update(
            update.Enabled, minSweep, update.Confirmations, update.ScanIntervalMinutes,
            updatedBy, timeProvider.GetUtcNow());
        if (applied.IsFailure)
            return Result.Failure<SweepSettingsView>(applied.Error!);

        await repository.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Sweep settings changed for {Chain} by {Actor}: enabled={Enabled}, threshold={Threshold}, "
            + "confirmations={Confirmations}, interval={Interval}m.",
            chain, updatedBy, update.Enabled, minSweep, update.Confirmations, update.ScanIntervalMinutes);

        return Result.Success(Project(settings));
    }

    public async Task<Result<SweepSettingsView>> RequestScanAsync(
        Chain chain, string requestedBy, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken);
        if (settings is null)
            return Result.Failure<SweepSettingsView>(SweepErrors.ChainNotConfigured);

        if (!settings.Enabled)
            return Result.Failure<SweepSettingsView>(SweepErrors.ChainPaused);

        settings.RequestScan(timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Sweep scan requested for {Chain} by {Actor}.", chain, requestedBy);
        return Result.Success(Project(settings));
    }

    /// <summary>
    /// Claims a pass when the schedule says one is due (or a human asked). Consumes the manual request in
    /// the same save, so one request produces one pass however many instances are polling.
    /// </summary>
    public async Task<bool> TryBeginScanAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken);
        if (settings is null)
            return false;

        var now = timeProvider.GetUtcNow();
        if (!settings.IsDue(now))
            return false;

        settings.BeginScan(now);
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task CompleteScanAsync(Chain chain, int sweepsCreated, CancellationToken cancellationToken = default)
    {
        var settings = await ResolveAsync(chain, cancellationToken);
        if (settings is null)
            return;

        settings.CompleteScan(sweepsCreated, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The chain's row, created from configuration on first use and re-synced from it while staff
    /// have never saved their own values. Null for a chain with no configured policy — never swept.</summary>
    private async Task<SweepSettings?> ResolveAsync(Chain chain, CancellationToken cancellationToken)
    {
        if (_loaded.TryGetValue(chain, out var cached))
            return cached;

        if (!defaults.Configured.TryGetValue(chain, out var configured))
            return null;

        var now = timeProvider.GetUtcNow();
        var settings = await repository.FindAsync(chain, cancellationToken);

        if (settings is null)
        {
            settings = await repository.AddOrGetAsync(
                SweepSettings.FromConfiguration(
                    chain, configured.MinSweepAmount, configured.Confirmations, configured.ScanIntervalMinutes, now),
                cancellationToken);
        }
        else
        {
            settings.TrackConfiguration(
                configured.MinSweepAmount, configured.Confirmations, configured.ScanIntervalMinutes, now);
            await repository.SaveChangesAsync(cancellationToken);
        }

        _loaded[chain] = settings;
        return settings;
    }

    private static SweepSettingsView Project(SweepSettings s) => new(
        s.Chain.ToString(),
        s.Enabled,
        s.MinSweepAmount.ToString(CultureInfo.InvariantCulture),
        s.Confirmations,
        s.ScanIntervalMinutes,
        s.IsStaffConfigured ? "Stored" : "Configuration",
        s.ScanRequestedAt,
        s.LastScanStartedAt,
        s.LastScanCompletedAt,
        s.LastSweepsCreated,
        s.UpdatedBy,
        s.UpdatedAt);
}
