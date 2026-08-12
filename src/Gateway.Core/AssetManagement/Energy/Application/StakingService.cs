using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Keeps the platform staking wallet's own energy topped up (policy-driven auto-stake). Each pass reads the
/// staking wallet's on-chain energy; if it is at/below the policy's stake threshold and auto-stake is enabled,
/// it creates a Pending Stake operation (the processing worker then freezes TRX). It moves no money itself and
/// posts no ledger entry — frozen TRX is recoverable (§15.4).
/// </summary>
public sealed class StakingService(
    StakingWalletLocator stakingWallets,
    IAccountResourceReader resources,
    IEnergyPolicyRepository policies,
    IEnergyOperationRepository operations,
    EnergyOperationOptions options,
    TimeProvider timeProvider,
    ILogger<StakingService> logger)
{
    public async Task<int> ReplenishAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
            return 0; // no (or ambiguous) staking wallet — nothing to replenish

        var policy = await policies.FindAsync(chain, StakingWalletLocator.EnergyWalletType, cancellationToken);
        if (policy is null || !policy.EnableAutoStake)
            return 0; // staking is opt-in per policy

        var observed = await resources.GetAsync(chain, staking.Address, cancellationToken);
        if (observed.EnergyAvailable > policy.StakeThreshold)
            return 0; // still above the stake trigger

        if (await operations.HasInFlightStakeAsync(staking.WalletId, cancellationToken))
            return 0; // one already moving

        var operation = EnergyOperation.CreateStake(
            staking.WalletId, chain, staking.Address, options.StakeIncrementTrxSun, timeProvider.GetUtcNow());
        if (operation.IsFailure)
            return 0;

        if (!await operations.TryAddAsync(operation.Value, cancellationToken))
            return 0; // lost the create race

        logger.LogInformation(
            "Auto-stake queued for {Chain} staking wallet {Address}: freezing {Trx} sun (energy {Energy} ≤ threshold {Threshold}).",
            chain, staking.Address, options.StakeIncrementTrxSun, observed.EnergyAvailable, policy.StakeThreshold);
        return 1;
    }
}
