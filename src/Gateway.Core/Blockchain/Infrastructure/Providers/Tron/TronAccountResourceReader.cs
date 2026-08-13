using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON <see cref="IAccountResourceReader"/> over the native <c>/wallet/*</c> API: <c>getaccountresource</c>
/// for energy + bandwidth standing, <c>getaccount</c> for the spendable TRX balance (which
/// <c>getaccountresource</c> does not return but the bandwidth-cushion gate needs). Amounts are exact base
/// units — energy/bandwidth in their own units, TRX in sun (§14). Read-only: no key ever crosses it (§10).
///
/// <para>Bandwidth combines the free daily allotment and any staked portion. Delegated-energy in/out is left
/// zero in this first cut (it needs the richer <c>account_resource</c> parse); it is observability only and
/// does not affect the sweep gate. NOTE: <c>getaccount.balance</c> is <em>spendable</em> TRX — frozen/
/// delegated TRX is excluded, which Reconciliation must account for once TRX becomes a reconciled asset.</para>
/// </summary>
public sealed class TronAccountResourceReader(ITronResourceRpc rpc, TimeProvider timeProvider) : IAccountResourceReader
{
    private const string EnergyResource = "ENERGY";

    public async Task<AccountResourceSnapshot> GetAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        if (chain != Chain.Tron)
            throw new ArgumentException($"TronAccountResourceReader cannot read {chain} resources.", nameof(chain));

        var ownerHex = TronAddress.ToRawHex(address); // 21-byte 41… form the native API expects (visible=false)

        var resource = await rpc.GetAccountResourceAsync(ownerHex, cancellationToken);
        var account = await rpc.GetAccountAsync(ownerHex, cancellationToken);

        var frozenForEnergy = account.FrozenV2
            .Where(f => string.Equals(f.Type, EnergyResource, StringComparison.OrdinalIgnoreCase))
            .Aggregate(BigInteger.Zero, (sum, f) => sum + f.Amount);

        // A frozenV2 entry with no "type" is a BANDWIDTH stake.
        var frozenForBandwidth = account.FrozenV2
            .Where(f => string.IsNullOrEmpty(f.Type))
            .Aggregate(BigInteger.Zero, (sum, f) => sum + f.Amount);

        return new AccountResourceSnapshot(
            chain,
            address,
            EnergyLimit: resource.EnergyLimit,
            EnergyUsed: resource.EnergyUsed,
            // Bandwidth = free daily allotment + staked portion.
            BandwidthLimit: (BigInteger)resource.FreeNetLimit + resource.NetLimit,
            BandwidthUsed: (BigInteger)resource.FreeNetUsed + resource.NetUsed,
            FrozenTrxForEnergy: frozenForEnergy,
            FrozenTrxForBandwidth: frozenForBandwidth,
            DelegatedEnergyOut: BigInteger.Zero, // observability only; richer account_resource parse deferred
            DelegatedEnergyIn: BigInteger.Zero,
            AvailableTrxBalance: account.Balance, // spendable TRX in sun (excludes frozen/delegated)
            ObservedAt: timeProvider.GetUtcNow());
    }
}
