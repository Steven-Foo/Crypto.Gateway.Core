using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Implements the public <see cref="IEnergyDelegationService"/> seam Sweep uses to ready a deposit address.
/// It reads the target's current energy and, if short, ensures a delegation exists (creating a Pending one
/// from the staking wallet if none is in flight). It is non-blocking — it returns
/// <see cref="EnergyReadiness.Provisioning"/> and lets Energy's own workers confirm the delegation while the
/// caller retries. It moves no money and posts no ledger entry (delegated energy is recoverable, §15.4).
/// </summary>
public sealed class EnergyDelegationService(
    StakingWalletLocator stakingWallets,
    IAccountResourceReader resources,
    IEnergyOperationRepository operations,
    EnergyOperationOptions options,
    TimeProvider timeProvider,
    ILogger<EnergyDelegationService> logger) : IEnergyDelegationService
{
    public async Task<EnergyReadiness> EnsureEnergyForTransferAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var observed = await resources.GetAsync(chain, address, cancellationToken);

        // 1. Energy — the expensive resource. A short TRC-20 transfer burns ~27 TRX, so we delegate rather than
        //    burn: ensure a delegation from the platform staking wallet, then have the caller retry.
        if (observed.EnergyAvailable < options.RequiredEnergyPerTransfer)
            return await EnsureEnergyDelegatedAsync(chain, address, observed.EnergyAvailable, cancellationToken);

        // 2. Bandwidth — the cheap resource (~0.27 TRX burn). We do NOT delegate it; the address pays it from
        //    free/staked bandwidth OR a small spendable-TRX cushion. If it has neither, the gas hub SUPPLIES the
        //    TRX (a top-up transfer) rather than letting the tx fail on-chain — the address then has a cushion.
        if (observed.BandwidthAvailable < options.RequiredBandwidthPerTransfer
            && observed.AvailableTrxBalance < options.MinTrxCushionSun)
        {
            return await EnsureBandwidthToppedUpAsync(chain, address, observed, cancellationToken);
        }

        return EnergyReadiness.Ready;
    }

    /// <summary>
    /// The gas hub supplies bandwidth-TRX: sends a small native-TRX top-up from the staking (gas hub) wallet to a
    /// short address so it can pay its own bandwidth. A real transfer between platform-controlled addresses
    /// (custody-internal ⇒ no ledger, §15.4). Non-blocking — creates a Pending TopUp and returns Provisioning; the
    /// energy workers confirm it and the caller retries. No staking wallet ⇒ Unavailable (nothing can top it up).
    /// </summary>
    private async Task<EnergyReadiness> EnsureBandwidthToppedUpAsync(
        Chain chain, string address, AccountResourceSnapshot observed, CancellationToken cancellationToken)
    {
        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
        {
            logger.LogWarning(
                "Address {Address} on {Chain} has energy but cannot pay bandwidth (bandwidth {Bandwidth}, TRX {Trx} sun) and no gas-hub wallet is registered to top it up.",
                address, chain, observed.BandwidthAvailable, observed.AvailableTrxBalance);
            return EnergyReadiness.Unavailable;
        }

        if (await operations.HasInFlightTopUpAsync(chain, address, cancellationToken))
            return EnergyReadiness.Provisioning; // already being topped up

        var operation = EnergyOperation.CreateTopUp(
            staking.WalletId, chain, staking.Address, address, options.TopUpTrxSun, timeProvider.GetUtcNow());
        if (operation.IsFailure)
            return EnergyReadiness.Unavailable;

        await operations.TryAddAsync(operation.Value, cancellationToken);

        logger.LogInformation(
            "Topping up {Trx} sun of TRX from the gas hub to {Address} on {Chain} for bandwidth (had bandwidth {Bandwidth}, TRX {Balance} sun).",
            options.TopUpTrxSun, address, chain, observed.BandwidthAvailable, observed.AvailableTrxBalance);
        return EnergyReadiness.Provisioning;
    }

    private async Task<EnergyReadiness> EnsureEnergyDelegatedAsync(
        Chain chain, string address, System.Numerics.BigInteger energyAvailable, CancellationToken cancellationToken)
    {
        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
        {
            // Can't delegate without a staking source — the caller keeps waiting rather than burning TRX.
            logger.LogWarning("Cannot delegate energy to {Address} on {Chain}: no staking wallet registered.", address, chain);
            return EnergyReadiness.Unavailable;
        }

        if (await operations.HasInFlightDelegateAsync(chain, address, cancellationToken))
            return EnergyReadiness.Provisioning; // already being provisioned

        var operation = EnergyOperation.CreateDelegate(
            staking.WalletId, chain, staking.Address, address, options.DelegateTrxSun, timeProvider.GetUtcNow());
        if (operation.IsFailure)
            return EnergyReadiness.Unavailable;

        // The unique in-flight-delegate index arbitrates the create race; a lost race just means someone else
        // is already provisioning this address — either way the answer is "provisioning, retry".
        await operations.TryAddAsync(operation.Value, cancellationToken);

        logger.LogInformation(
            "Delegating {Trx} sun of energy to {Address} on {Chain} (had {Energy}, needs {Required}).",
            options.DelegateTrxSun, address, chain, energyAvailable, options.RequiredEnergyPerTransfer);
        return EnergyReadiness.Provisioning;
    }
}
