using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>
/// Finds deposit addresses worth sweeping and creates a <see cref="Domain.Sweep"/> for each. For every active
/// asset on the chain it reads each funded deposit address's on-chain balance (<see cref="IBalanceReader"/>)
/// and, when the balance clears the policy threshold and no sweep is already in flight for it, creates a
/// Pending sweep into a <b>cold collection wallet</b> (the custody anchor). It moves no money itself (the
/// processing service builds/signs/broadcasts) and holds no keys. Reaches other modules only through their
/// Contracts (§4.5).
///
/// <para><b>Taint segregation.</b> When <c>Sweep:Screening</c> is enabled, the deposit address is screened
/// before the sweep is created and the verdict picks the destination: clean balances go to the Safe
/// collection wallet, flagged ones to the Danger wallet. The decision has to be made here, before the
/// transfer, because once two balances share an address they cannot be separated again — everything that
/// later leaves that address carries the taint. A flagged balance is never sent to the Safe wallet as a
/// fallback; if there is nowhere segregated to put it, it stays on the deposit address, which the platform
/// also controls.</para>
/// </summary>
public sealed class SweepScanService(
    IAssetCatalog assets,
    IWalletDirectory wallets,
    IBalanceReader balances,
    ITreasuryColdWalletDirectory coldWallets,
    ISweepPolicyProvider policies,
    ISweepRepository repository,
    TimeProvider timeProvider,
    IOptions<SweepScreeningOptions> screeningOptions,
    ILogger<SweepScanService> logger,
    IAddressScreeningService? screening = null)
{
    public async Task<int> ScanAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var safeWallet = await coldWallets.GetAsync(chain, ColdWalletKind.Safe, cancellationToken);
        if (safeWallet.IsFailure)
        {
            // No destination ⇒ nothing to sweep into. Stay inert (same posture as a missing signer/config).
            logger.LogWarning(
                "Sweep scan skipped for {Chain}: no Safe cold collection wallet registered ({Error}).",
                chain, safeWallet.Error!.Message);
            return 0;
        }

        var policy = await policies.ForAsync(chain, cancellationToken);
        var active = await assets.GetActiveAsync(cancellationToken);
        var addresses = await wallets.ListReceivingDepositAddressesAsync(chain, cancellationToken);
        var routing = new SweepRouting(chain, safeWallet.Value, screeningOptions.Value, screening, coldWallets, logger);
        var created = 0;

        foreach (var asset in active.Where(a => a.Chain == chain))
        {
            // Balances first, routing second: screening is only worth spending on an address that is
            // actually about to be swept, which is a far smaller set than every funded address.
            var candidates = new List<(ReceivingDepositAddress Address, BigInteger Balance)>();
            foreach (var address in addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var balance = await TryReadSweepableBalanceAsync(chain, asset.AssetId, address, policy, cancellationToken);
                if (balance is not null)
                    candidates.Add((address, balance.Value));
            }

            if (candidates.Count == 0)
                continue;

            await routing.PrepareAsync([.. candidates.Select(c => c.Address.Address)], cancellationToken);

            foreach (var (address, balance) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryCreateSweepAsync(chain, asset.AssetId, address, balance, routing, cancellationToken))
                    created++;
            }
        }

        return created;
    }

    /// <summary>The address's balance when it is worth sweeping, else null. Never throws: one unreadable
    /// address or transient failure must not stop the scan — the next pass retries it.</summary>
    private async Task<BigInteger?> TryReadSweepableBalanceAsync(
        Chain chain, Guid assetId, ReceivingDepositAddress address, SweepPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            // Belt (the unique index is the real arbiter): don't create a second sweep for an address that
            // already has one moving.
            if (await repository.HasInFlightAsync(address.WalletId, assetId, cancellationToken))
                return null;

            var balance = await balances.GetBalanceAsync(chain, address.Address, assetId, cancellationToken);
            return policy.ShouldSweep(balance) ? balance : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sweep scan skipped {Address} on {Chain}.", address.Address, chain);
            return null;
        }
    }

    private async Task<bool> TryCreateSweepAsync(
        Chain chain, Guid assetId, ReceivingDepositAddress address, BigInteger balance,
        SweepRouting routing, CancellationToken cancellationToken)
    {
        try
        {
            var route = await routing.ResolveAsync(address.Address, cancellationToken);
            if (route is null)
                return false; // Held — no destination for this address's class yet. Funds stay put.

            var sweep = Domain.Sweep.Create(
                address.WalletId, chain, assetId, address.Address, route.Destination, balance,
                route.Kind, route.ScreeningId, route.ScreeningDecision, timeProvider.GetUtcNow());
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
            logger.LogWarning(ex, "Sweep scan skipped {Address} on {Chain}.", address.Address, chain);
            return false;
        }
    }
}

/// <summary>Where one address's balance should go, and the evidence for that.</summary>
internal sealed record SweepRoute(
    string Destination, SweepDestinationKind Kind, Guid? ScreeningId, string? ScreeningDecision);

/// <summary>
/// Decides, per deposit address, which collection wallet its balance goes to. Lives beside the scan rather
/// than inside it so the budgeting is visible in one place: the provider allows about one call a second and
/// the payout gate shares the quota, so a pass screens at most
/// <see cref="SweepScreeningOptions.MaxScreeningsPerPass"/> addresses and lets the rest wait.
/// </summary>
internal sealed class SweepRouting(
    Chain chain,
    ColdTreasuryWallet safeWallet,
    SweepScreeningOptions options,
    IAddressScreeningService? screening,
    ITreasuryColdWalletDirectory coldWallets,
    ILogger logger)
{
    private readonly HashSet<string> _screenThisPass = new(StringComparer.Ordinal);
    private ColdTreasuryWallet? _dangerWallet;
    private bool _dangerResolved;

    /// <summary>Works out, in ONE query, which of this pass's candidates lack a fresh verdict — then takes
    /// only as many of them as the budget allows. Everything else is served from stored evidence, so a pass
    /// over a hundred addresses is not a hundred provider calls.</summary>
    public async Task PrepareAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        _screenThisPass.Clear();

        if (!options.Enabled || screening is null || options.MaxScreeningsPerPass <= 0)
            return;

        var stale = await screening.FindAddressesNeedingScreeningAsync(
            chain, addresses, options.MaxScreeningsPerPass, cancellationToken);

        foreach (var address in stale)
            _screenThisPass.Add(address);
    }

    /// <summary>The destination for one address, or null when the sweep must be held.</summary>
    public async Task<SweepRoute?> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return new SweepRoute(safeWallet.Address, SweepDestinationKind.Safe, null, null);

        if (screening is null)
        {
            // Configured to segregate with nothing to segregate by. Never silently sweep everything into
            // clean treasury on the strength of a missing dependency — say so and hold.
            logger.LogError(
                "Sweep screening is enabled for {Chain} but no screening provider is composed in this host; sweeps are held.",
                chain);
            return null;
        }

        var verdict = _screenThisPass.Contains(address)
            ? await screening.ScreenAsync(chain, address, ScreeningPurpose.DepositAddress, cancellationToken)
            : await screening.FindLatestAsync(chain, address, cancellationToken);

        return verdict?.Decision switch
        {
            ScreeningDecision.Allow =>
                new SweepRoute(safeWallet.Address, SweepDestinationKind.Safe, verdict.ScreeningId, verdict.Decision.ToString()),

            // Review and Block both quarantine. Segregating a clean balance is reversible by a human moving
            // it out; mixing a tainted one into clean treasury is not reversible at all.
            ScreeningDecision.Review or ScreeningDecision.Block =>
                await DangerRouteAsync(verdict.ScreeningId, verdict.Decision.ToString(), cancellationToken),

            // No verdict at all, or one that failed: the vendor is down, quota is spent, or the address has
            // never been screened.
            _ => await FallbackRouteAsync(address, verdict, cancellationToken),
        };
    }

    private async Task<SweepRoute?> FallbackRouteAsync(
        string address, ScreeningVerdict? verdict, CancellationToken cancellationToken)
    {
        var id = verdict?.ScreeningId;
        var decision = verdict?.Decision.ToString();

        switch (options.OnUnavailable)
        {
            case UnscreenedSweepAction.Safe:
                return new SweepRoute(safeWallet.Address, SweepDestinationKind.Safe, id, decision);

            case UnscreenedSweepAction.Danger:
                return await DangerRouteAsync(id, decision, cancellationToken);

            default:
                logger.LogInformation(
                    "Sweep held for {Address} on {Chain}: no screening verdict available.", address, chain);
                return null;
        }
    }

    private async Task<SweepRoute?> DangerRouteAsync(
        Guid? screeningId, string? decision, CancellationToken cancellationToken)
    {
        if (!_dangerResolved)
        {
            var result = await coldWallets.GetAsync(chain, ColdWalletKind.Danger, cancellationToken);
            _dangerWallet = result.IsSuccess ? result.Value : null;
            _dangerResolved = true;
        }

        if (_dangerWallet is not null)
            return new SweepRoute(_dangerWallet.Address, SweepDestinationKind.Danger, screeningId, decision);

        // No quarantine address registered. Holding leaves the balance on a deposit address the platform
        // controls; the alternative — falling back to the Safe wallet — is the exact contamination this
        // feature exists to prevent, so it is never done.
        logger.LogWarning(
            "Sweep held on {Chain}: a flagged deposit address needs the Danger collection wallet, and none is registered.",
            chain);
        return null;
    }
}
