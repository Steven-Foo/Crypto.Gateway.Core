using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Takes back energy the staking wallet lent to deposit addresses, so the same staked TRX can serve the next
/// address. Without this, every sweep ties up another slice of the stake for good, and the stake runs out.
///
/// <para><b>What it reclaims:</b> an address whose latest lend confirmed at least
/// <see cref="EnergyOperationOptions.ReclaimAfter"/> ago, with no take-back since and no lend in progress. The
/// amount is read from the CHAIN, not added up from our records, so one take-back returns everything however
/// many lends built it up, and nothing is ever double-counted.</para>
///
/// <para><b>What it leaves alone:</b> the platform's own wallets (the hot pool, and the staking wallet itself).
/// Hot wallets pay out again and again, so they keep their energy, and a take-back can never land in the middle
/// of a payout. A deposit address whose sweep is still waiting just borrows again — that costs one extra
/// transaction, never money.</para>
///
/// <para>No ledger entry: the TRX never leaves the platform (§15.4). The undelegate is built, signed and sent by
/// the same processing worker as every other energy operation.</para>
/// </summary>
public sealed class EnergyReclaimService(
    StakingWalletLocator stakingWallets,
    IPlatformWalletDirectory platformWallets,
    IDelegatedResourceReader delegations,
    IEnergyOperationRepository operations,
    EnergyOperationOptions options,
    TimeProvider timeProvider,
    ILogger<EnergyReclaimService> logger)
{
    public async Task<int> ReclaimOnceAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var staking = await stakingWallets.FindAsync(chain, cancellationToken);
        if (staking is null)
            return 0;

        var platformAddresses = (await platformWallets.GetPlatformWalletsAsync(chain, cancellationToken))
            .Select(w => w.Address)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var candidates = await operations.ListReclaimCandidatesAsync(
            chain, staking.WalletId, now - options.ReclaimAfter, platformAddresses, options.ReclaimBatchSize, cancellationToken);

        var created = 0;
        foreach (var target in candidates)
        {
            var lentSun = await delegations.GetDelegatedEnergySunAsync(chain, staking.Address, target, cancellationToken);

            var operation = lentSun > 0
                ? EnergyOperation.CreateUndelegate(staking.WalletId, chain, staking.Address, target, lentSun, now)
                : EnergyOperation.RecordNothingToReclaim(staking.WalletId, chain, staking.Address, target, now);
            if (operation.IsFailure)
                continue;

            if (!await operations.TryAddAsync(operation.Value, cancellationToken))
                continue;

            if (lentSun > 0)
            {
                created++;
                logger.LogInformation(
                    "Taking back {Sun} sun of lent energy from {Address} on {Chain}.", lentSun, target, chain);
            }
            else
            {
                logger.LogInformation(
                    "Nothing is lent to {Address} on {Chain} any more; recorded so it is not checked again.", target, chain);
            }
        }

        return created;
    }
}
