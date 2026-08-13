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

    /// <summary>Bandwidth an unset address reports — comfortably above a transfer's need, so the dev sweep gate
    /// reads Ready without any TRX cushion. A test that exercises the bandwidth path sets an explicit snapshot.</summary>
    public BigInteger DefaultBandwidthAvailable { get; set; } = 1_000_000;

    /// <summary>Spendable TRX (sun) an unset address reports — a healthy cushion so the dev gate reads Ready.</summary>
    public BigInteger DefaultTrxBalanceSun { get; set; } = 100_000_000; // 100 TRX

    public void Set(Chain chain, string address, AccountResourceSnapshot snapshot) =>
        _byAddress[Key(chain, address)] = snapshot;

    /// <summary>Convenience: pin the available energy for an address, keeping bandwidth and TRX comfortably
    /// healthy — so a test exercises energy in isolation without tripping the bandwidth/TRX side of the gate.
    /// A test that specifically probes the bandwidth path drives an explicit <see cref="Set"/> snapshot.</summary>
    public void SetEnergyAvailable(Chain chain, string address, BigInteger energyAvailable) =>
        Set(chain, address, new AccountResourceSnapshot(
            chain, address,
            EnergyLimit: energyAvailable, EnergyUsed: BigInteger.Zero,
            BandwidthLimit: DefaultBandwidthAvailable, BandwidthUsed: BigInteger.Zero,
            FrozenTrxForEnergy: BigInteger.Zero, FrozenTrxForBandwidth: BigInteger.Zero,
            DelegatedEnergyOut: BigInteger.Zero, DelegatedEnergyIn: BigInteger.Zero,
            AvailableTrxBalance: DefaultTrxBalanceSun, timeProvider.GetUtcNow()));

    public Task<AccountResourceSnapshot> GetAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        if (_byAddress.TryGetValue(Key(chain, address), out var snapshot))
            return Task.FromResult(snapshot with { ObservedAt = timeProvider.GetUtcNow() });

        return Task.FromResult(new AccountResourceSnapshot(
            chain, address,
            EnergyLimit: DefaultEnergyAvailable, EnergyUsed: BigInteger.Zero,
            BandwidthLimit: DefaultBandwidthAvailable, BandwidthUsed: BigInteger.Zero,
            FrozenTrxForEnergy: BigInteger.Zero, FrozenTrxForBandwidth: BigInteger.Zero,
            DelegatedEnergyOut: BigInteger.Zero, DelegatedEnergyIn: BigInteger.Zero,
            AvailableTrxBalance: DefaultTrxBalanceSun, timeProvider.GetUtcNow()));
    }

    private static string Key(Chain chain, string address) => $"{chain}:{address}";
}
