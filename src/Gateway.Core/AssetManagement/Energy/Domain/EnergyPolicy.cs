using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;

/// <summary>
/// The energy automation rules for one class of platform wallet on one chain: the levels the monitor
/// watches now (Phase 5a) and the stake/rent triggers it will act on later (5b).
///
/// Energy is a TRON-specific resource, so <see cref="Chain"/> is always <c>Tron</c> today — the column
/// exists so the policy generalises without a schema change if another chain ever needs it. This
/// aggregate holds POLICY only; it never moves TRX, delegates energy, or writes a ledger entry.
/// Evaluation against a live resource snapshot lives in the Application layer (§4.6).
///
/// <see cref="WalletType"/> is a string, matching how wallet type crosses the module boundary
/// (<c>WalletOwnership.WalletType</c>) — Energy never references the Wallet module's enum (§4.5).
/// </summary>
public sealed class EnergyPolicy : Entity<Guid>
{
    private EnergyPolicy(
        Guid id,
        Chain chain,
        string walletType,
        BigInteger minimumEnergy,
        BigInteger targetEnergy,
        BigInteger stakeThreshold,
        BigInteger rentalThreshold,
        bool enableAutoStake,
        bool enableAutoRent,
        bool isEnabled,
        DateTimeOffset createdAt) : base(id)
    {
        Chain = chain;
        WalletType = walletType;
        MinimumEnergy = minimumEnergy;
        TargetEnergy = targetEnergy;
        StakeThreshold = stakeThreshold;
        RentalThreshold = rentalThreshold;
        EnableAutoStake = enableAutoStake;
        EnableAutoRent = enableAutoRent;
        IsEnabled = isEnabled;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private EnergyPolicy() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }
    public string WalletType { get; private set; } = null!;

    /// <summary>Energy units. Below this the wallet is <see cref="ResourceHealth.Critical"/>.</summary>
    public BigInteger MinimumEnergy { get; private set; }

    /// <summary>Energy units. The level 5b replenishes up to; below it (but ≥ minimum) is <see cref="ResourceHealth.Low"/>.</summary>
    public BigInteger TargetEnergy { get; private set; }

    /// <summary>5b: at/below this, prefer staking more TRX (when <see cref="EnableAutoStake"/>).</summary>
    public BigInteger StakeThreshold { get; private set; }

    /// <summary>5b: at/below this, prefer renting energy (when <see cref="EnableAutoRent"/>).</summary>
    public BigInteger RentalThreshold { get; private set; }

    public bool EnableAutoStake { get; private set; }
    public bool EnableAutoRent { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<EnergyPolicy> Create(
        Chain chain,
        string walletType,
        BigInteger minimumEnergy,
        BigInteger targetEnergy,
        BigInteger stakeThreshold,
        BigInteger rentalThreshold,
        bool enableAutoStake,
        bool enableAutoRent,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(walletType))
            return Result.Failure<EnergyPolicy>(EnergyErrors.WalletTypeRequired);

        if (minimumEnergy < 0 || targetEnergy < 0 || stakeThreshold < 0 || rentalThreshold < 0)
            return Result.Failure<EnergyPolicy>(EnergyErrors.NegativeThreshold);

        if (targetEnergy < minimumEnergy)
            return Result.Failure<EnergyPolicy>(EnergyErrors.TargetBelowMinimum);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        return Result.Success(new EnergyPolicy(
            Guid.CreateVersion7(), chain, walletType.Trim(), minimumEnergy, targetEnergy,
            stakeThreshold, rentalThreshold, enableAutoStake, enableAutoRent, isEnabled: true, now));
    }

    public Result Update(
        BigInteger minimumEnergy,
        BigInteger targetEnergy,
        BigInteger stakeThreshold,
        BigInteger rentalThreshold,
        bool enableAutoStake,
        bool enableAutoRent,
        DateTimeOffset now)
    {
        if (minimumEnergy < 0 || targetEnergy < 0 || stakeThreshold < 0 || rentalThreshold < 0)
            return Result.Failure(EnergyErrors.NegativeThreshold);

        if (targetEnergy < minimumEnergy)
            return Result.Failure(EnergyErrors.TargetBelowMinimum);

        MinimumEnergy = minimumEnergy;
        TargetEnergy = targetEnergy;
        StakeThreshold = stakeThreshold;
        RentalThreshold = rentalThreshold;
        EnableAutoStake = enableAutoStake;
        EnableAutoRent = enableAutoRent;
        UpdatedAt = now;
        return Result.Success();
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        IsEnabled = enabled;
        UpdatedAt = now;
    }

    /// <summary>
    /// Classifies an observed energy level against this policy. Pure — no I/O, no side effects. This is
    /// the whole of Phase 5a's decision logic; 5b maps <see cref="ResourceHealth.Low"/>/<c>Critical</c>
    /// onto a delegate/stake/rent action.
    /// </summary>
    public ResourceHealth Classify(BigInteger availableEnergy) =>
        availableEnergy < MinimumEnergy ? ResourceHealth.Critical
        : availableEnergy < TargetEnergy ? ResourceHealth.Low
        : ResourceHealth.Healthy;
}
