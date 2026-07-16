using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Phase 5a — the whole of the Energy module's behaviour: for each platform wallet on a chain, read its
/// on-chain resources, classify them against the wallet's <see cref="EnergyPolicy"/>, persist an
/// observability snapshot + history, and log an alert when energy is Low/Critical.
///
/// It is strictly read-and-record: it never freezes TRX, delegates energy, moves money, or writes a ledger
/// entry (§4.6, and the caller's rule that Energy touches no money path). The automated response to a Low/
/// Critical wallet — delegate / stake / rent — arrives in Phase 5b behind its own signing boundary (§10).
/// </summary>
public sealed class ResourceMonitorService(
    IPlatformWalletDirectory wallets,
    IAccountResourceReader resources,
    IEnergyPolicyRepository policies,
    IWalletResourceStore resourceStore,
    IResourceHistoryStore historyStore,
    ILogger<ResourceMonitorService> logger)
{
    public async Task MonitorAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var platformWallets = await wallets.GetPlatformWalletsAsync(chain, cancellationToken);
        if (platformWallets.Count == 0)
            return;

        foreach (var wallet in platformWallets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MonitorWalletAsync(chain, wallet, cancellationToken);
        }
    }

    private async Task MonitorWalletAsync(Chain chain, PlatformWallet wallet, CancellationToken cancellationToken)
    {
        try
        {
            var observed = await resources.GetAsync(chain, wallet.Address, cancellationToken);
            var policy = await policies.FindAsync(chain, wallet.WalletType, cancellationToken);

            // No policy → observe without classifying: still recorded for visibility, but nothing to alert on.
            var health = policy?.Classify(observed.EnergyAvailable) ?? ResourceHealth.Healthy;

            var snapshot = new WalletResourceSnapshot(
                wallet.WalletId, chain, wallet.Address, wallet.WalletType, health,
                observed.EnergyAvailable, observed.EnergyLimit, observed.EnergyUsed, observed.BandwidthAvailable,
                observed.FrozenTrxForEnergy, observed.FrozenTrxForBandwidth,
                observed.DelegatedEnergyOut, observed.DelegatedEnergyIn, observed.AvailableTrxBalance,
                policy?.TargetEnergy, policy?.MinimumEnergy, observed.ObservedAt);

            await resourceStore.UpsertAsync(snapshot, cancellationToken);
            await historyStore.AppendAsync(snapshot, cancellationToken);

            if (health != ResourceHealth.Healthy)
            {
                logger.LogWarning(
                    "Energy {Health} on {WalletType} wallet {Address}: available {Available}, target {Target}, minimum {Minimum}.",
                    health, wallet.WalletType, wallet.Address, observed.EnergyAvailable,
                    policy!.TargetEnergy, policy.MinimumEnergy);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable node or bad wallet must not stop the batch — the next poll retries it.
            logger.LogWarning(ex, "Resource monitor skipped {WalletType} wallet {Address}.", wallet.WalletType, wallet.Address);
        }
    }
}
