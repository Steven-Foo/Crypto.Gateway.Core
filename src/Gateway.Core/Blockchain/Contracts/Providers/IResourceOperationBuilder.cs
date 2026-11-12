using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>Freeze/stake native balance to acquire energy (TRON <c>FreezeBalanceV2</c>, ENERGY resource).
/// <paramref name="TrxAmountSun"/> is the TRX to freeze, in sun (base units, §14). Frozen TRX is not spent —
/// it stays the owner's, recoverable via unstake — so this operation is not an expense.</summary>
public sealed record StakeForEnergyRequest(Chain Chain, string OwnerAddress, BigInteger TrxAmountSun);

/// <summary>Delegate staked energy from <paramref name="OwnerAddress"/> to <paramref name="ReceiverAddress"/>
/// (TRON <c>DelegateResourceContract</c>, ENERGY). <paramref name="TrxAmountSun"/> is the staked-balance amount
/// to delegate (its energy yield depends on the network price). Reclaimable via undelegate — not spent.</summary>
public sealed record DelegateEnergyRequest(Chain Chain, string OwnerAddress, string ReceiverAddress, BigInteger TrxAmountSun);

/// <summary>Take back energy lent to <paramref name="ReceiverAddress"/> (TRON <c>UnDelegateResourceContract</c>,
/// ENERGY). <paramref name="TrxAmountSun"/> is the staked amount to take back; it returns to the owner's staked
/// balance immediately, ready to lend again. Nothing is spent.</summary>
public sealed record UndelegateEnergyRequest(Chain Chain, string OwnerAddress, string ReceiverAddress, BigInteger TrxAmountSun);

/// <summary>Start unlocking staked TRX (TRON <c>UnfreezeBalanceV2</c>, ENERGY). The TRX becomes withdrawable
/// only after the network's waiting period (14 days on mainnet); until then it earns no energy.</summary>
public sealed record UnstakeEnergyRequest(Chain Chain, string OwnerAddress, BigInteger TrxAmountSun);

/// <summary>Move every unstake whose waiting period has ended back into the owner's spendable balance (TRON
/// <c>WithdrawExpireUnfreeze</c>). The node decides the amount; there is nothing to choose.</summary>
public sealed record WithdrawUnstakedRequest(Chain Chain, string OwnerAddress);

/// <summary>
/// Builds unsigned resource-management transactions — staking and delegating energy (§8). Read/compute only:
/// it never signs, so a module that builds these still cannot move/lock funds without the separate
/// <c>ISigner</c> (§10). The built blob flows through the same <c>ISigner</c>/<c>ITransactionBroadcaster</c> as
/// a transfer. TRON-specific: energy/staking has no Ethereum/Solana analogue (gas there is just native balance),
/// so today only a TRON adapter exists; the port stays chain-parameterised for future generalisation.
/// </summary>
public interface IResourceOperationBuilder
{
    Task<UnsignedTransaction> BuildStakeForEnergyAsync(StakeForEnergyRequest request, CancellationToken cancellationToken = default);

    Task<UnsignedTransaction> BuildDelegateEnergyAsync(DelegateEnergyRequest request, CancellationToken cancellationToken = default);

    Task<UnsignedTransaction> BuildUndelegateEnergyAsync(UndelegateEnergyRequest request, CancellationToken cancellationToken = default);

    Task<UnsignedTransaction> BuildUnstakeEnergyAsync(UnstakeEnergyRequest request, CancellationToken cancellationToken = default);

    Task<UnsignedTransaction> BuildWithdrawUnstakedAsync(WithdrawUnstakedRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads how much staked TRX an owner currently lends to one receiver as ENERGY — the chain's own figure, so a
/// take-back returns exactly what is out, however many delegations built it up. Read-only (§10).
/// </summary>
public interface IDelegatedResourceReader
{
    /// <summary>Staked TRX (sun) <paramref name="ownerAddress"/> lends to <paramref name="receiverAddress"/> as
    /// energy. Zero when nothing is lent.</summary>
    Task<BigInteger> GetDelegatedEnergySunAsync(
        Chain chain, string ownerAddress, string receiverAddress, CancellationToken cancellationToken = default);
}
