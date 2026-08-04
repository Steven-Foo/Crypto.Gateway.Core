using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

/// <summary>The source (hot) wallet a withdrawal is paid from: its address and the signing-key reference.</summary>
public sealed record HotWallet(string Address, string KeyReference);

/// <summary>
/// Resolves the hot wallet a withdrawal is paid from, for a chain. Backed by the Treasury module, which
/// owns hot-wallet registration and selection; multi-wallet rebalancing remains a future refinement.
/// Throws if no hot wallet is registered — a withdrawal must never be built without a known source.
/// </summary>
public interface IHotWalletProvider
{
    Task<HotWallet> ForAsync(Chain chain, CancellationToken cancellationToken = default);
}
