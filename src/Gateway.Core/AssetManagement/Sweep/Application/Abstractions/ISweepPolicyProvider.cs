using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;

/// <summary>Resolves the sweep policy for a chain. A per-asset (rather than per-chain) policy is a future
/// refinement, mirroring Withdrawal.</summary>
public interface ISweepPolicyProvider
{
    SweepPolicy For(Chain chain);
}
