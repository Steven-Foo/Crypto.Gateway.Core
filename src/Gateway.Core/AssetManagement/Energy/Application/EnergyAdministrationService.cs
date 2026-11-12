using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <param name="Created">False when the chain already had its staking wallet and it was returned unchanged.</param>
public sealed record StakingWalletProvisioning(Chain Chain, Guid WalletId, string Address, bool Created);

/// <summary>The staking (energy) wallet's rules as staff see them. All energy figures are TRON energy units.</summary>
public sealed record StakingPolicyView(
    Chain Chain, BigInteger MinimumEnergy, BigInteger TargetEnergy, BigInteger StakeThreshold, bool EnableAutoStake,
    DateTimeOffset UpdatedAt);

public sealed record SetStakingPolicyCommand(
    Chain Chain, BigInteger MinimumEnergy, BigInteger TargetEnergy, BigInteger StakeThreshold, bool EnableAutoStake);

/// <summary>Serialises staking-wallet creation per chain across hosts and instances.</summary>
public interface IEnergyProvisioningLock
{
    Task<IAsyncDisposable> AcquireAsync(Chain chain, CancellationToken cancellationToken = default);
}

/// <summary>
/// The staff actions behind the energy settings screen: create the chain's staking wallet (the TRX gas hub that
/// stakes TRX for energy and lends it to deposit and hot-pool addresses), and read or change its rules.
///
/// <para><b>Exactly one staking wallet per chain.</b> <see cref="StakingWalletLocator"/> refuses to pick between
/// two, so a second wallet would silently stop all staking. Creation therefore runs under a per-chain distributed
/// lock and re-checks inside it; asking again returns the existing wallet. In production the first call mints
/// and seals the energy seed under its own KMS key; the address returned holds nothing until it is funded.</para>
///
/// <para>No ledger entry anywhere here: creating an address and changing thresholds move no money (§14).</para>
/// </summary>
public sealed class EnergyAdministrationService(
    StakingWalletLocator stakingWallets,
    IPlatformWalletDirectory platformWallets,
    IWalletDerivation derivation,
    IPlatformWalletRegistrar walletRegistrar,
    IEnergyPolicyRepository policies,
    IEnergyOperationRepository operations,
    IEnergyProvisioningLock provisioningLock,
    TimeProvider timeProvider,
    ILogger<EnergyAdministrationService> logger)
{
    public async Task<Result<StakingWalletProvisioning>> ProvisionStakingWalletAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        await using var _ = await provisioningLock.AcquireAsync(chain, cancellationToken);

        var existing = (await platformWallets.GetPlatformWalletsAsync(chain, cancellationToken))
            .Where(w => w.WalletType == StakingWalletLocator.EnergyWalletType)
            .ToList();
        if (existing.Count > 1)
            return Result.Failure<StakingWalletProvisioning>(EnergyErrors.StakingWalletAmbiguous);
        if (existing.Count == 1)
            return Result.Success(new StakingWalletProvisioning(chain, existing[0].WalletId, existing[0].Address, Created: false));

        var derived = await derivation.AllocateNextAsync(chain, DerivationPurpose.Energy, cancellationToken);
        if (derived.IsFailure)
            return Result.Failure<StakingWalletProvisioning>(derived.Error!);

        var registered = await walletRegistrar.RegisterPlatformWalletAsync(
            derived.Value.DerivedKeyId, chain, derived.Value.Address, StakingWalletLocator.EnergyWalletType,
            "Platform staking (energy) wallet", cancellationToken);
        if (registered.IsFailure)
            return Result.Failure<StakingWalletProvisioning>(registered.Error!);

        logger.LogInformation("Created the {Chain} staking (energy) wallet {Address}.", chain, derived.Value.Address);
        return Result.Success(new StakingWalletProvisioning(chain, registered.Value.WalletId, registered.Value.Address, Created: true));
    }

    public Task<StakingWallet?> FindStakingWalletAsync(Chain chain, CancellationToken cancellationToken = default) =>
        stakingWallets.FindAsync(chain, cancellationToken);

    public async Task<StakingPolicyView?> GetPolicyAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var policy = await policies.FindAsync(chain, StakingWalletLocator.EnergyWalletType, cancellationToken);
        return policy is null ? null : View(policy);
    }

    /// <summary>Creates or updates the staking wallet's rules. Rental is not built, so it stays off.</summary>
    public async Task<Result<StakingPolicyView>> SetPolicyAsync(
        SetStakingPolicyCommand command, CancellationToken cancellationToken = default)
    {
        var policy = await policies.FindAsync(command.Chain, StakingWalletLocator.EnergyWalletType, cancellationToken);
        if (policy is null)
        {
            var created = EnergyPolicy.Create(
                command.Chain, StakingWalletLocator.EnergyWalletType, command.MinimumEnergy, command.TargetEnergy,
                command.StakeThreshold, rentalThreshold: BigInteger.Zero, command.EnableAutoStake, enableAutoRent: false,
                timeProvider);
            if (created.IsFailure)
                return Result.Failure<StakingPolicyView>(created.Error!);

            policy = created.Value;
            policies.Add(policy);
        }
        else
        {
            var updated = policy.Update(
                command.MinimumEnergy, command.TargetEnergy, command.StakeThreshold, rentalThreshold: BigInteger.Zero,
                command.EnableAutoStake, enableAutoRent: false, timeProvider.GetUtcNow());
            if (updated.IsFailure)
                return Result.Failure<StakingPolicyView>(updated.Error!);
        }

        await policies.SaveChangesAsync(cancellationToken);
        return Result.Success(View(policy));
    }

    /// <summary>
    /// Starts unlocking <paramref name="trxAmountSun"/> of staked TRX. Refused while auto-stake is on (the drop in
    /// energy would trigger a new stake straight away) and while another unstake is in progress. The chain itself
    /// refuses more than is staked and not lent out; that shows up as a failed operation with the node's reason.
    /// </summary>
    public async Task<Result<Guid>> RequestUnstakeAsync(
        Chain chain, BigInteger trxAmountSun, CancellationToken cancellationToken = default)
    {
        await using var _ = await provisioningLock.AcquireAsync(chain, cancellationToken);

        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
            return Result.Failure<Guid>(EnergyErrors.StakingWalletNotFound);

        var policy = await policies.FindAsync(chain, StakingWalletLocator.EnergyWalletType, cancellationToken);
        if (policy is { EnableAutoStake: true })
            return Result.Failure<Guid>(EnergyErrors.AutoStakeStillOn);

        if (await operations.HasInFlightAsync(staking.WalletId, EnergyOperationKind.Unstake, cancellationToken))
            return Result.Failure<Guid>(EnergyErrors.OperationAlreadyInFlight);

        var operation = EnergyOperation.CreateUnstake(staking.WalletId, chain, staking.Address, trxAmountSun, timeProvider.GetUtcNow());
        if (operation.IsFailure)
            return Result.Failure<Guid>(operation.Error!);

        await operations.TryAddAsync(operation.Value, cancellationToken);
        logger.LogInformation("Unstake of {Sun} sun requested for the {Chain} staking wallet.", trxAmountSun, chain);
        return Result.Success(operation.Value.Id);
    }

    /// <summary>Moves every finished unstake back into the staking wallet's spendable TRX. If nothing has finished
    /// its waiting period yet, the chain refuses and the operation shows as failed with that reason.</summary>
    public async Task<Result<Guid>> RequestWithdrawUnstakedAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        await using var _ = await provisioningLock.AcquireAsync(chain, cancellationToken);

        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
            return Result.Failure<Guid>(EnergyErrors.StakingWalletNotFound);

        if (await operations.HasInFlightAsync(staking.WalletId, EnergyOperationKind.WithdrawUnstaked, cancellationToken))
            return Result.Failure<Guid>(EnergyErrors.OperationAlreadyInFlight);

        var operation = EnergyOperation.CreateWithdrawUnstaked(staking.WalletId, chain, staking.Address, timeProvider.GetUtcNow());
        if (operation.IsFailure)
            return Result.Failure<Guid>(operation.Error!);

        await operations.TryAddAsync(operation.Value, cancellationToken);
        logger.LogInformation("Withdrawal of finished unstakes requested for the {Chain} staking wallet.", chain);
        return Result.Success(operation.Value.Id);
    }

    private static StakingPolicyView View(EnergyPolicy policy) => new(
        policy.Chain, policy.MinimumEnergy, policy.TargetEnergy, policy.StakeThreshold, policy.EnableAutoStake, policy.UpdatedAt);
}
