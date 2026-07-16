using System.Collections.Concurrent;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// A deterministic, in-memory <see cref="IAccountResourceReader"/> for Development and tests — the same DI
/// seam the real TRON <c>getaccountresource</c> adapter plugs into (§8). A test drives an address's
/// resources via <see cref="Set"/>; an unset address returns a comfortably-healthy default so the monitor
/// has something to observe in dev. Thread-safe: the monitor worker and a test may touch it concurrently.
/// </summary>
public sealed class InMemoryAccountResourceReader(TimeProvider timeProvider) : IAccountResourceReader
{
    private readonly ConcurrentDictionary<string, AccountResourceSnapshot> _byAddress =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Energy an unset address reports as available. Large enough to read Healthy against dev policy.</summary>
    public BigInteger DefaultEnergyAvailable { get; set; } = 10_000_000;

    public void Set(Chain chain, string address, AccountResourceSnapshot snapshot) =>
        _byAddress[Key(chain, address)] = snapshot;

    /// <summary>Convenience: pin only the available energy for an address, leaving other fields at zero.</summary>
    public void SetEnergyAvailable(Chain chain, string address, BigInteger energyAvailable) =>
        Set(chain, address, new AccountResourceSnapshot(
            chain, address,
            EnergyLimit: energyAvailable, EnergyUsed: BigInteger.Zero,
            BandwidthLimit: BigInteger.Zero, BandwidthUsed: BigInteger.Zero,
            FrozenTrxForEnergy: BigInteger.Zero, FrozenTrxForBandwidth: BigInteger.Zero,
            DelegatedEnergyOut: BigInteger.Zero, DelegatedEnergyIn: BigInteger.Zero,
            AvailableTrxBalance: BigInteger.Zero, timeProvider.GetUtcNow()));

    public Task<AccountResourceSnapshot> GetAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        if (_byAddress.TryGetValue(Key(chain, address), out var snapshot))
            return Task.FromResult(snapshot with { ObservedAt = timeProvider.GetUtcNow() });

        return Task.FromResult(new AccountResourceSnapshot(
            chain, address,
            EnergyLimit: DefaultEnergyAvailable, EnergyUsed: BigInteger.Zero,
            BandwidthLimit: BigInteger.Zero, BandwidthUsed: BigInteger.Zero,
            FrozenTrxForEnergy: BigInteger.Zero, FrozenTrxForBandwidth: BigInteger.Zero,
            DelegatedEnergyOut: BigInteger.Zero, DelegatedEnergyIn: BigInteger.Zero,
            AvailableTrxBalance: BigInteger.Zero, timeProvider.GetUtcNow()));
    }

    private static string Key(Chain chain, string address) => $"{chain}:{address}";
}
