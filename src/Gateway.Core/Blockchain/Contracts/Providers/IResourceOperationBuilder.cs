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
}
