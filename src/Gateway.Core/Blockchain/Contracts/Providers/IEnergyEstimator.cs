using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

public sealed record EstimateTransferEnergyRequest(Chain Chain, Guid AssetId, string FromAddress, string ToAddress, BigInteger Amount);

/// <summary>
/// <see cref="EnergyUsed"/> is the real, current cost of THIS specific transfer (this recipient, this
/// amount, against chain state right now) — not the fixed platform-wide safety margin
/// (<c>EnergyOperationOptions.RequiredEnergyPerTransfer</c>) used to gate an actual send. A brand-new
/// recipient costs meaningfully more than one who already holds the token, which is exactly what this
/// captures and a fixed threshold cannot. <see cref="WouldSucceed"/>/<see cref="FailureReason"/> report
/// whether the simulated call would actually complete (e.g. an insufficient sender balance reverts here too).
/// </summary>
public sealed record EnergyEstimate(bool WouldSucceed, BigInteger EnergyUsed, string? FailureReason);

/// <summary>
/// Simulates a transfer — no signature, no broadcast, no funds moved, no cost — to find out how much energy
/// it would actually take, before committing to send it for real. Lets an operator (or, later, an automated
/// decision) choose between using staked/delegated energy and renting from a third party, sized to the real
/// need instead of the conservative fixed default. §8 capability segregation: only a chain whose node exposes
/// a constant/simulated-call RPC implements this; it is simply absent elsewhere.
/// </summary>
public interface IEnergyEstimator
{
    Task<EnergyEstimate> EstimateTransferEnergyAsync(EstimateTransferEnergyRequest request, CancellationToken cancellationToken = default);
}
