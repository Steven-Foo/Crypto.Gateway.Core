using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// A single chain's read-only adapter — the concrete realisation of the capability ports for one chain.
/// <see cref="RoutingChainSource"/> dispatches the multi-chain <see cref="IDepositScanner"/>/
/// <see cref="IChainStatusReader"/> to the right adapter by <see cref="Chain"/> (the §8
/// <c>IBlockchainProviderFactory.For(chain)</c> pattern).
/// </summary>
public interface IChainAdapter : IDepositScanner, IChainStatusReader
{
    Chain Chain { get; }
}
