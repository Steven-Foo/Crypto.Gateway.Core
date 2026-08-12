using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>
/// Finds deposit addresses worth sweeping and creates a <see cref="Domain.Sweep"/> for each. For every active
/// asset on the chain, it reads each funded deposit address's on-chain balance (<see cref="IBalanceReader"/>)
/// and, when the balance clears the policy threshold and no sweep is already in flight for it, creates a
/// Pending sweep into the <b>cold treasury</b> (the custody anchor) — funds then leave the hot deposit tier for
/// cold storage, and the hot withdrawal pool is refilled from treasury by a human-signed reload (Phase 2). It
/// moves no money itself (the processing service builds/signs/broadcasts) and holds no keys. Reaches other
/// modules only through their Contracts (§4.5).
/// </summary>
public sealed class SweepScanService(
    IAssetCatalog assets,
    IWalletDirectory wallets,
    IBalanceReader balances,
    ITreasuryColdWalletDirectory coldWallets,
    ISweepPolicyProvider policies,
    ISweepRepository repository,
    TimeProvider timeProvider,
    ILogger<SweepScanService> logger)
{
    public async Task<int> ScanAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var coldWallet = await coldWallets.GetAsync(chain, cancellationToken);
        if (coldWallet.IsFailure)
        {
            // No destination ⇒ nothing to sweep into. Stay inert (same posture as a missing signer/config).
            logger.LogWarning("Sweep scan skipped for {Chain}: no cold treasury registered ({Error}).", chain, coldWallet.Error!.Message);
            return 0;
        }

        var policy = policies.For(chain);
        var active = await assets.GetActiveAsync(cancellationToken);
        var created = 0;

        foreach (var asset in active.Where(a => a.Chain == chain))
        {
            var addresses = await wallets.ListReceivingDepositAddressesAsync(chain, cancellationToken);
            foreach (var address in addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryCreateSweepAsync(chain, asset.AssetId, address, coldWallet.Value.Address, policy, cancellationToken))
                    created++;
            }
        }

        return created;
    }

    private async Task<bool> TryCreateSweepAsync(
        Chain chain, Guid assetId, ReceivingDepositAddress address, string destination, SweepPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            // Belt (the unique index is the real arbiter): don't create a second sweep for an address that
            // already has one moving.
            if (await repository.HasInFlightAsync(address.WalletId, assetId, cancellationToken))
                return false;

            var balance = await balances.GetBalanceAsync(chain, address.Address, assetId, cancellationToken);
            if (!policy.ShouldSweep(balance))
                return false;

            var sweep = Domain.Sweep.Create(
                address.WalletId, chain, assetId, address.Address, destination, balance, timeProvider.GetUtcNow());
            if (sweep.IsFailure)
                return false;

            // TryAdd loses the create race (unique one-in-flight index) → false; we simply drop it.
            return await repository.TryAddAsync(sweep.Value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreadable address or transient failure must not stop the scan — the next pass retries it.
            logger.LogWarning(ex, "Sweep scan skipped {Address} on {Chain}.", address.Address, chain);
            return false;
        }
    }
}
