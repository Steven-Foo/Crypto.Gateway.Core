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

    // ── 5b: stake/delegate operations ──
    public static readonly Error OperationOwnerRequired =
        Error.Validation("energy.operation_owner_required", "An energy operation requires an owner (staking) address.");

    public static readonly Error DelegateReceiverRequired =
        Error.Validation("energy.delegate_receiver_required", "A delegate operation requires a receiver address.");

    public static readonly Error OperationAmountNotPositive =
        Error.Validation("energy.operation_amount_not_positive", "An energy operation amount must be positive.");

    public static readonly Error OperationInvalidStateTransition =
        Error.Conflict("energy.operation_invalid_state", "The energy operation is not in a state that allows this transition.");
}
