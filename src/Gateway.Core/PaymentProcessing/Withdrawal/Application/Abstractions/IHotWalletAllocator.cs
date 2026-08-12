using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

/// <summary>A pool wallet leased for one payout: its Wallet-row id (stamped on the withdrawal), its address,
/// and the signing-key reference. <see cref="KeyReference"/> is a reference, never key material (§10).</summary>
public sealed record HotWalletLease(Guid WalletId, string Address, string KeyReference);

/// <summary>
/// Picks an available hot wallet from the platform pool for a payout. "Available" = not currently leased (no
/// in-flight withdrawal is signing/broadcasting from it — one transaction at a time per wallet, held until it
/// confirms) AND holding at least <paramref name="amount"/> on-chain. Among the eligible wallets it chooses
/// the least-recently-used, spreading payouts across the pool. Returns <c>null</c> when none is available
/// (all busy or underfunded) — the caller then parks the withdrawal (AwaitingFunds) and retries, exactly as
/// it does for an underfunded single wallet. Backed by the Treasury pool + the withdrawal repo + a balance
/// reader, each through Contracts (§4.5).
/// </summary>
public interface IHotWalletAllocator
{
    Task<HotWalletLease?> AllocateAsync(
        Chain chain, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default);
}
