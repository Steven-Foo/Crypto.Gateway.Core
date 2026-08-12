using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;

/// <summary>
/// Allocates a hot-pool wallet for a payout, backed by the Treasury pool directory + the withdrawal repo +
/// a balance reader — each through Contracts (§4.5). Excludes wallets already leased (an in-flight withdrawal
/// is signing/broadcasting from them — one tx at a time, held until confirmed), keeps only those holding at
/// least the amount on-chain, and picks the least-recently-used, spreading payouts across the pool. Returns
/// null when none is available so the processing service parks the withdrawal (AwaitingFunds) and retries.
///
/// A free wallet has no in-flight outflow of its own, so its readable on-chain balance is exact — no
/// in-flight subtraction is needed (the one-tx-per-wallet lease provides the isolation the single-wallet
/// float check used to get from summing committed outflows).
/// </summary>
public sealed class HotWalletAllocator(
    ITreasuryHotWalletDirectory treasury,
    IWithdrawalRepository repository,
    IBalanceReader balances) : IHotWalletAllocator
{
    public async Task<HotWalletLease?> AllocateAsync(
        Chain chain, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default)
    {
        var pool = await treasury.GetHotWalletPoolAsync(chain, cancellationToken);
        if (pool.Count == 0)
            return null;

        var busy = await repository.GetInFlightSourceWalletIdsAsync(chain, cancellationToken);
        var free = pool.Where(w => !busy.Contains(w.WalletId)).ToList();
        if (free.Count == 0)
            return null;

        var funded = new List<TreasuryHotWallet>();
        foreach (var wallet in free)
        {
            var balance = await balances.GetBalanceAsync(chain, wallet.Address, assetId, cancellationToken);
            if (balance >= amount)
                funded.Add(wallet);
        }

        if (funded.Count == 0)
            return null;

        // Least-recently-used first (never-used ⇒ MinValue ⇒ most eligible); address as a stable tiebreak.
        var lastUsed = await repository.GetWalletLastUsedAsync(funded.Select(w => w.WalletId).ToList(), cancellationToken);
        var chosen = funded
            .OrderBy(w => lastUsed.TryGetValue(w.WalletId, out var used) ? used : DateTimeOffset.MinValue)
            .ThenBy(w => w.Address, StringComparer.Ordinal)
            .First();

        return new HotWalletLease(chosen.WalletId, chosen.Address, chosen.KeyReference);
    }
}
