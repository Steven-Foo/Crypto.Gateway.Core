using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

/// <summary>The source (hot) wallet a withdrawal is paid from: its address and the signing-key reference.</summary>
public sealed record HotWallet(string Address, string KeyReference);

/// <summary>
/// Resolves the hot wallet for a chain. For P1 this is a configured wallet per chain; proper hot-wallet
/// selection/rebalancing across multiple wallets is the Treasury module's job (a future refinement).
/// </summary>
public interface IHotWalletProvider
{
    HotWallet For(Chain chain);
}
