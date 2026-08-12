using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;

/// <summary>
/// The whole of the Reconciliation module's behaviour (first cut): for each active asset on a chain, compare
/// the ledger's <c>TreasuryAsset</c> holding against the summed on-chain balance across every address the
/// platform controls (its platform wallets + every deposit address that has received funds), record an
/// observability snapshot + history, and log the outcome.
///
/// It is strictly read-and-record: it moves no money, holds no keys, and writes no ledger entry (§4.6, §5).
/// The ledger stays the single source of truth (§15.4); the chain is only external state used to cross-check
/// it (§15.5). It reaches other modules only through their Contracts (§4.5): <c>ILedgerQuery</c> (Ledger),
/// <c>IBalanceReader</c>/<c>IAssetCatalog</c> (Blockchain), <c>IPlatformWalletDirectory</c>/
/// <c>IWalletDirectory</c> (Wallet).
/// </summary>
public sealed class ReconciliationService(
    ILedgerQuery ledger,
    IBalanceReader balances,
    IAssetCatalog assets,
    IPlatformWalletDirectory platformWallets,
    IWalletDirectory depositWallets,
    ITreasuryColdWalletDirectory coldTreasury,
    IReconciliationStore store,
    IReconciliationHistoryStore history,
    ReconciliationOptions options,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger)
{
    public async Task ReconcileAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var active = await assets.GetActiveAsync(cancellationToken);

        foreach (var asset in active.Where(a => a.Chain == chain))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileAssetAsync(chain, asset, cancellationToken);
        }
    }

    private async Task ReconcileAssetAsync(Chain chain, AssetDto asset, CancellationToken cancellationToken)
    {
        var ledgerHolding = await ledger.GetTreasuryHoldingAsync(asset.AssetId, cancellationToken);
        var addresses = await GatherControlledAddressesAsync(chain, cancellationToken);

        var onChainTotal = BigInteger.Zero;
        var unreadable = 0;

        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                onChainTotal += await balances.GetBalanceAsync(chain, address, asset.AssetId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable address must not abort the pass — but it does make the total partial, so the
                // pass is reported Incomplete rather than a false Balanced/Drift.
                unreadable++;
                logger.LogWarning(ex,
                    "Reconciliation could not read {Asset} balance at {Address} on {Chain}; excluding it from the on-chain total.",
                    asset.Symbol, address, chain);
            }
        }

        var drift = onChainTotal - ledgerHolding;
        var status = unreadable > 0
            ? ReconciliationStatus.Incomplete
            : BigInteger.Abs(drift) <= options.DriftTolerance
                ? ReconciliationStatus.Balanced
                : ReconciliationStatus.Drift;

        var snapshot = new ReconciliationSnapshot(
            chain, asset.AssetId, asset.Symbol, ledgerHolding, onChainTotal, drift, status,
            addresses.Count, unreadable, timeProvider.GetUtcNow());

        await store.UpsertAsync(snapshot, cancellationToken);
        await history.AppendAsync(snapshot, cancellationToken);

        LogOutcome(snapshot);
    }

    private async Task<IReadOnlyList<string>> GatherControlledAddressesAsync(Chain chain, CancellationToken cancellationToken)
    {
        var platform = await platformWallets.GetPlatformWalletsAsync(chain, cancellationToken);
        var deposits = await depositWallets.ListReceivingDepositAddressesAsync(chain, cancellationToken);

        // Platform wallets and deposit addresses are disjoint by design, but dedup defensively so an address
        // can never be double-counted into the on-chain total (that would invent or hide drift).
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wallet in platform)
            unique.Add(wallet.Address);
        foreach (var deposit in deposits)
            unique.Add(deposit.Address);

        // The cold treasury holds most of the custody post-sweep, and it is not a Wallet-module row (it is
        // watch-only, keyless — homed in Treasury). Include it, or reconciliation would report a huge false
        // drift once sweeping concentrates funds there.
        var cold = await coldTreasury.GetAsync(chain, cancellationToken);
        if (cold.IsSuccess)
            unique.Add(cold.Value.Address);

        return [.. unique];
    }

    private void LogOutcome(ReconciliationSnapshot s)
    {
        switch (s.Status)
        {
            case ReconciliationStatus.Balanced:
                logger.LogInformation(
                    "Reconciliation balanced for {Asset} on {Chain}: ledger {Ledger} == on-chain {OnChain} across {Count} address(es).",
                    s.AssetSymbol, s.Chain, s.LedgerHolding, s.OnChainTotal, s.AddressesScanned);
                break;

            case ReconciliationStatus.Drift:
                logger.LogWarning(
                    "Reconciliation DRIFT for {Asset} on {Chain}: on-chain {OnChain} − ledger {Ledger} = {Drift} across {Count} address(es). "
                    + "Investigate — a still-unconfirmed deposit or an in-flight withdrawal can cause transient drift; a sustained drift cannot.",
                    s.AssetSymbol, s.Chain, s.OnChainTotal, s.LedgerHolding, s.Drift, s.AddressesScanned);
                break;

            case ReconciliationStatus.Incomplete:
                logger.LogWarning(
                    "Reconciliation INCOMPLETE for {Asset} on {Chain}: {Unreadable} of {Count} address(es) could not be read, so on-chain {OnChain} is partial and the drift {Drift} vs ledger {Ledger} is unreliable.",
                    s.AssetSymbol, s.Chain, s.AddressesUnreadable, s.AddressesScanned, s.OnChainTotal, s.Drift, s.LedgerHolding);
                break;
        }
    }
}
