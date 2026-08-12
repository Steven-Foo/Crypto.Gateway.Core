using System.Text;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// A deterministic, in-memory <see cref="IResourceOperationBuilder"/> for Development and tests — the DI seam
/// the real TRON <c>FreezeBalanceV2</c>/<c>DelegateResourceContract</c> adapter replaces (§8). It computes a
/// stable, opaque unsigned blob (no node, no key); the in-memory signer/broadcaster then carry it exactly like
/// a transfer, so the whole stake/delegate lifecycle runs end to end in dev without a live TRON node.
/// </summary>
public sealed class InMemoryResourceOperationBuilder : IResourceOperationBuilder
{
    public Task<UnsignedTransaction> BuildStakeForEnergyAsync(StakeForEnergyRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UnsignedTransaction(
            Encode("stake", request.Chain.ToString(), request.OwnerAddress, request.OwnerAddress, request.TrxAmountSun.ToString())));

    public Task<UnsignedTransaction> BuildDelegateEnergyAsync(DelegateEnergyRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UnsignedTransaction(
            Encode("delegate", request.Chain.ToString(), request.OwnerAddress, request.ReceiverAddress, request.TrxAmountSun.ToString())));

    private static byte[] Encode(string kind, string chain, string owner, string receiver, string amount) =>
        Encoding.UTF8.GetBytes($"{kind}:{chain}:{owner}:{receiver}:{amount}");
}
