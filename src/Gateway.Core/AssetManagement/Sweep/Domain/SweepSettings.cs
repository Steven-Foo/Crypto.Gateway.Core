using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

/// <summary>
/// One chain's sweep configuration and the state of its schedule: the economic threshold, the confirmation
/// depth, how often a scan runs, whether it runs at all, and when it last did.
///
/// <para><b>Why this is a row and not just configuration.</b> The threshold and the interval are operational
/// dials an operator needs to turn while the platform is running — a gas spike makes a low threshold
/// uneconomic within the hour. Config remains the floor: a chain's row is created from
/// <c>Sweep:Policies:{chain}</c> and keeps tracking it until staff save a value, after which the row wins
/// and <see cref="IsStaffConfigured"/> says so. That distinction matters on a settings screen, where
/// "nobody has set this" and "someone set it to exactly the default" look identical otherwise.</para>
///
/// <para><b>Enabled is deliberately here rather than in config.</b> Unlike a screening master switch, which
/// stays in configuration because turning it off removes a control that holds money, pausing sweeps is the
/// opposite: it stops funds moving. The cautious direction is reachable from the back office, and the
/// dangerous one (sending funds) still requires the destination wallets and the signer.</para>
/// </summary>
public sealed class SweepSettings : Entity<Guid>
{
    private SweepSettings(
        Guid id, Chain chain, bool enabled, BigInteger minSweepAmount, int confirmations,
        int scanIntervalMinutes, DateTimeOffset now) : base(id)
    {
        Chain = chain;
        Enabled = enabled;
        MinSweepAmount = minSweepAmount;
        Confirmations = confirmations;
        ScanIntervalMinutes = scanIntervalMinutes;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private SweepSettings() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }

    /// <summary>Whether the scheduled scan runs. False pauses concentration: balances stay on deposit
    /// addresses, which the platform also controls, so nothing is at risk while it is off.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Base units (§14). A balance below this is left alone, so gas is never spent concentrating
    /// dust worth less than the transfer.</summary>
    public BigInteger MinSweepAmount { get; private set; }

    /// <summary>On-chain depth a broadcast sweep must reach before it is treated as final.</summary>
    public int Confirmations { get; private set; }

    public int ScanIntervalMinutes { get; private set; }

    /// <summary>False while the values still track configuration; true once staff have saved them here.</summary>
    public bool IsStaffConfigured { get; private set; }

    /// <summary>Set by the manual-trigger action, cleared when the next pass picks it up. A request rather
    /// than a direct call: the host that serves the back office runs no sweep workers and holds no chain
    /// credentials (§4.7), so it asks the money host to scan rather than scanning itself.</summary>
    public DateTimeOffset? ScanRequestedAt { get; private set; }

    public DateTimeOffset? LastScanStartedAt { get; private set; }
    public DateTimeOffset? LastScanCompletedAt { get; private set; }

    /// <summary>Sweeps created by the last completed pass — the number that tells an operator whether a run
    /// did anything, without reading the sweep list.</summary>
    public int? LastSweepsCreated { get; private set; }

    public string? UpdatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static SweepSettings FromConfiguration(
        Chain chain, BigInteger minSweepAmount, int confirmations, int scanIntervalMinutes, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), chain, enabled: true, minSweepAmount, confirmations,
            NormalizeInterval(scanIntervalMinutes), now);

    /// <summary>Re-syncs from configuration while staff have never saved a value here. Does nothing once
    /// they have — an operator's decision must not be silently reverted by a deployment.</summary>
    public void TrackConfiguration(BigInteger minSweepAmount, int confirmations, int scanIntervalMinutes, DateTimeOffset now)
    {
        if (IsStaffConfigured)
            return;

        var interval = NormalizeInterval(scanIntervalMinutes);
        if (MinSweepAmount == minSweepAmount && Confirmations == confirmations && ScanIntervalMinutes == interval)
            return;

        MinSweepAmount = minSweepAmount;
        Confirmations = confirmations;
        ScanIntervalMinutes = interval;
        UpdatedAt = now;
    }

    public Result Update(
        bool enabled, BigInteger minSweepAmount, int confirmations, int scanIntervalMinutes,
        string updatedBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(updatedBy))
            return Result.Failure(SweepErrors.ActorRequired);

        // A negative threshold would sweep nothing meaningfully; zero is valid and means "sweep any balance",
        // which is a real (if gas-hungry) choice on a cheap chain.
        if (minSweepAmount < BigInteger.Zero)
            return Result.Failure(SweepErrors.ThresholdNegative);

        // Zero confirmations would treat an unconfirmed transfer as final — a reorg would then leave the
        // platform's own books claiming funds moved when they did not.
        if (confirmations < 1)
            return Result.Failure(SweepErrors.ConfirmationsNotPositive);

        if (scanIntervalMinutes < MinScanIntervalMinutes || scanIntervalMinutes > MaxScanIntervalMinutes)
            return Result.Failure(SweepErrors.ScanIntervalOutOfRange);

        Enabled = enabled;
        MinSweepAmount = minSweepAmount;
        Confirmations = confirmations;
        ScanIntervalMinutes = scanIntervalMinutes;
        IsStaffConfigured = true;
        UpdatedBy = updatedBy.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Asks for a pass as soon as the money host next looks, without waiting for the interval.</summary>
    public void RequestScan(DateTimeOffset now)
    {
        ScanRequestedAt = now;
        UpdatedAt = now;
    }

    /// <summary>True when the schedule is due, or a human asked for a pass. A disabled chain is never due —
    /// including for a manual request, so "paused" means paused.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (!Enabled)
            return false;

        if (ScanRequestedAt is not null)
            return true;

        return LastScanStartedAt is null
            || now - LastScanStartedAt.Value >= TimeSpan.FromMinutes(ScanIntervalMinutes);
    }

    /// <summary>Marks a pass as started and consumes any manual request, so the request is honoured exactly
    /// once however many instances are polling.</summary>
    public void BeginScan(DateTimeOffset now)
    {
        ScanRequestedAt = null;
        LastScanStartedAt = now;
        UpdatedAt = now;
    }

    public void CompleteScan(int sweepsCreated, DateTimeOffset now)
    {
        LastScanCompletedAt = now;
        LastSweepsCreated = sweepsCreated;
        UpdatedAt = now;
    }

    public const int MinScanIntervalMinutes = 1;

    /// <summary>A week. Beyond this the schedule is not a schedule; a chain that should not be swept is
    /// paused with <see cref="Enabled"/> instead, which says so plainly on the screen.</summary>
    public const int MaxScanIntervalMinutes = 10_080;

    private static int NormalizeInterval(int minutes) =>
        Math.Clamp(minutes, MinScanIntervalMinutes, MaxScanIntervalMinutes);
}
