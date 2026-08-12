using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;

/// <summary>The platform's cold treasury wallet for a chain — a watch-only address whose key is not held by
/// the system (a human signs outbound transfers). It is the sweep destination and the reload source.</summary>
public sealed record ColdTreasuryWallet(Chain Chain, string Address);

/// <summary>
/// The read seam other modules consume to learn the cold treasury address: Sweep (its destination) and
/// Reconciliation (a controlled address to include in the custody sum). Fails when none is registered — the
/// caller then stays inert rather than moving funds to nowhere (§10).
/// </summary>
public interface ITreasuryColdWalletDirectory
{
    Task<Result<ColdTreasuryWallet>> GetAsync(Chain chain, CancellationToken cancellationToken = default);
}
