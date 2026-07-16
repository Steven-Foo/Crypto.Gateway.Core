using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;

public static class EnergyErrors
{
    public static readonly Error WalletTypeRequired =
        Error.Validation("energy.wallet_type_required", "A policy must target a wallet type.");

    public static readonly Error NegativeThreshold =
        Error.Validation("energy.negative_threshold", "Energy thresholds cannot be negative.");

    public static readonly Error TargetBelowMinimum =
        Error.Validation("energy.target_below_minimum", "Target energy must be greater than or equal to minimum energy.");

    public static readonly Error PolicyNotFound =
        Error.NotFound("energy.policy_not_found", "No energy policy is configured for this wallet type.");
}
