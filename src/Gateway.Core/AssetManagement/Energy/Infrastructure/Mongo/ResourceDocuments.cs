using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;

/// <summary>
/// The latest observed resources for one wallet (Mongo collection <c>WalletResource</c>), keyed by wallet id.
/// Base-unit amounts are stored as strings so a <see cref="BigInteger"/> never loses precision or overflows
/// a BSON numeric type — this is observability, not money, but precision is cheap to keep (§14 spirit).
/// </summary>
public sealed class WalletResourceDocument
{
    [BsonId] public string Id { get; set; } = null!; // WalletId as string — the upsert key
    public string Chain { get; set; } = null!;
    public string Address { get; set; } = null!;
    public string WalletType { get; set; } = null!;
    public string Health { get; set; } = null!;
    public string EnergyAvailable { get; set; } = null!;
    public string EnergyLimit { get; set; } = null!;
    public string EnergyUsed { get; set; } = null!;
    public string BandwidthAvailable { get; set; } = null!;
    public string FrozenTrxForEnergy { get; set; } = null!;
    public string FrozenTrxForBandwidth { get; set; } = null!;
    public string DelegatedEnergyOut { get; set; } = null!;
    public string DelegatedEnergyIn { get; set; } = null!;
    public string AvailableTrxBalance { get; set; } = null!;
    public string? TargetEnergy { get; set; }
    public string? MinimumEnergy { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>One appended observation (Mongo collection <c>ResourceHistory</c>) — the 5c forecasting feed.</summary>
public sealed class ResourceHistoryDocument
{
    [BsonId] public ObjectId Id { get; set; }
    public string WalletId { get; set; } = null!;
    public string Chain { get; set; } = null!;
    public string Address { get; set; } = null!;
    public string WalletType { get; set; } = null!;
    public string Health { get; set; } = null!;
    public string EnergyAvailable { get; set; } = null!;
    public string BandwidthAvailable { get; set; } = null!;
    public string AvailableTrxBalance { get; set; } = null!;
    public DateTimeOffset ObservedAt { get; set; }
}

internal static class ResourceDocumentMapper
{
    public static WalletResourceDocument ToCurrent(WalletResourceSnapshot s) => new()
    {
        Id = s.WalletId.ToString(),
        Chain = s.Chain.ToString(),
        Address = s.Address,
        WalletType = s.WalletType,
        Health = s.Health.ToString(),
        EnergyAvailable = s.EnergyAvailable.ToString(),
        EnergyLimit = s.EnergyLimit.ToString(),
        EnergyUsed = s.EnergyUsed.ToString(),
        BandwidthAvailable = s.BandwidthAvailable.ToString(),
        FrozenTrxForEnergy = s.FrozenTrxForEnergy.ToString(),
        FrozenTrxForBandwidth = s.FrozenTrxForBandwidth.ToString(),
        DelegatedEnergyOut = s.DelegatedEnergyOut.ToString(),
        DelegatedEnergyIn = s.DelegatedEnergyIn.ToString(),
        AvailableTrxBalance = s.AvailableTrxBalance.ToString(),
        TargetEnergy = s.TargetEnergy?.ToString(),
        MinimumEnergy = s.MinimumEnergy?.ToString(),
        ObservedAt = s.ObservedAt,
    };

    public static ResourceHistoryDocument ToHistory(WalletResourceSnapshot s) => new()
    {
        WalletId = s.WalletId.ToString(),
        Chain = s.Chain.ToString(),
        Address = s.Address,
        WalletType = s.WalletType,
        Health = s.Health.ToString(),
        EnergyAvailable = s.EnergyAvailable.ToString(),
        BandwidthAvailable = s.BandwidthAvailable.ToString(),
        AvailableTrxBalance = s.AvailableTrxBalance.ToString(),
        ObservedAt = s.ObservedAt,
    };

    public static WalletResourceSnapshot FromCurrent(WalletResourceDocument d) => new(
        Guid.Parse(d.Id),
        Enum.Parse<Chain>(d.Chain),
        d.Address,
        d.WalletType,
        Enum.Parse<ResourceHealth>(d.Health),
        BigInteger.Parse(d.EnergyAvailable),
        BigInteger.Parse(d.EnergyLimit),
        BigInteger.Parse(d.EnergyUsed),
        BigInteger.Parse(d.BandwidthAvailable),
        BigInteger.Parse(d.FrozenTrxForEnergy),
        BigInteger.Parse(d.FrozenTrxForBandwidth),
        BigInteger.Parse(d.DelegatedEnergyOut),
        BigInteger.Parse(d.DelegatedEnergyIn),
        BigInteger.Parse(d.AvailableTrxBalance),
        d.TargetEnergy is null ? null : BigInteger.Parse(d.TargetEnergy),
        d.MinimumEnergy is null ? null : BigInteger.Parse(d.MinimumEnergy),
        d.ObservedAt);
}
