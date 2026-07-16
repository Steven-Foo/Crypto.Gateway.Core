using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// One observation of a wallet's resources, enriched with its classified <see cref="Health"/> and the
/// policy thresholds in force. This is what the monitor upserts to the resource store and appends to
/// history — a derived, observational record, never a ledger balance (§2). Amounts are exact base units
/// (energy/bandwidth units; TRX in sun) as <see cref="BigInteger"/>.
/// </summary>
public sealed record WalletResourceSnapshot(
    Guid WalletId,
    Chain Chain,
    string Address,
    string WalletType,
    ResourceHealth Health,
    BigInteger EnergyAvailable,
    BigInteger EnergyLimit,
    BigInteger EnergyUsed,
    BigInteger BandwidthAvailable,
    BigInteger FrozenTrxForEnergy,
    BigInteger FrozenTrxForBandwidth,
    BigInteger DelegatedEnergyOut,
    BigInteger DelegatedEnergyIn,
    BigInteger AvailableTrxBalance,
    BigInteger? TargetEnergy,
    BigInteger? MinimumEnergy,
    DateTimeOffset ObservedAt);
