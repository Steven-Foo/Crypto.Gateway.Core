using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.SharedKernel;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Infrastructure.Mongo;

/// <summary>
/// The latest reconciliation snapshot for one (chain, asset) — Mongo collection <c>Reconciliation</c>, keyed
/// by <c>"{chain}:{assetId}"</c>. Base-unit amounts are stored as strings so a <see cref="BigInteger"/> never
/// loses precision or overflows a BSON numeric type — this is observability, not money, but precision is
/// cheap to keep (§14 spirit).
/// </summary>
public sealed class ReconciliationDocument
{
    [BsonId] public string Id { get; set; } = null!; // "{Chain}:{AssetId}" — the upsert key
    public string Chain { get; set; } = null!;
    public string AssetId { get; set; } = null!;
    public string AssetSymbol { get; set; } = null!;
    public string LedgerHolding { get; set; } = null!;
    public string OnChainTotal { get; set; } = null!;
    public string Drift { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int AddressesScanned { get; set; }
    public int AddressesUnreadable { get; set; }

    // Where the custody physically sits. Base-unit strings like every other amount here; default "0" so a
    // document written before these existed reads back as zeros rather than failing to parse.
    public string ColdTreasuryTotal { get; set; } = "0";
    public string HotPoolTotal { get; set; } = "0";
    public string DepositAddressTotal { get; set; } = "0";
    public string ToppedUpTotal { get; set; } = "0";
    // Stored as a real BSON date, NOT the driver's default for DateTimeOffset — a bare DateTimeOffset
    // serialises as a nested { DateTime, Ticks, Offset } document, which the collection's `bsonType: "date"`
    // validator rejects, so every snapshot write silently failed. UTC observations, so no offset is lost.
    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>One appended reconciliation observation (Mongo collection <c>ReconciliationHistory</c>) — the
/// audit trail of custody drift over time.</summary>
public sealed class ReconciliationHistoryDocument
{
    [BsonId] public ObjectId Id { get; set; }
    public string Chain { get; set; } = null!;
    public string AssetId { get; set; } = null!;
    public string AssetSymbol { get; set; } = null!;
    public string LedgerHolding { get; set; } = null!;
    public string OnChainTotal { get; set; } = null!;
    public string Drift { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int AddressesScanned { get; set; }
    public int AddressesUnreadable { get; set; }

    // Where the custody physically sits. Base-unit strings like every other amount here; default "0" so a
    // document written before these existed reads back as zeros rather than failing to parse.
    public string ColdTreasuryTotal { get; set; } = "0";
    public string HotPoolTotal { get; set; } = "0";
    public string DepositAddressTotal { get; set; } = "0";
    public string ToppedUpTotal { get; set; } = "0";
    // Stored as a real BSON date, NOT the driver's default for DateTimeOffset — a bare DateTimeOffset
    // serialises as a nested { DateTime, Ticks, Offset } document, which the collection's `bsonType: "date"`
    // validator rejects, so every snapshot write silently failed. UTC observations, so no offset is lost.
    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset ObservedAt { get; set; }
}

internal static class ReconciliationDocumentMapper
{
    public static string KeyOf(Chain chain, Guid assetId) => $"{chain}:{assetId}";

    public static ReconciliationDocument ToCurrent(ReconciliationSnapshot s) => new()
    {
        Id = KeyOf(s.Chain, s.AssetId),
        Chain = s.Chain.ToString(),
        AssetId = s.AssetId.ToString(),
        AssetSymbol = s.AssetSymbol,
        LedgerHolding = s.LedgerHolding.ToString(),
        OnChainTotal = s.OnChainTotal.ToString(),
        Drift = s.Drift.ToString(),
        Status = s.Status.ToString(),
        AddressesScanned = s.AddressesScanned,
        AddressesUnreadable = s.AddressesUnreadable,
        ColdTreasuryTotal = s.ColdTreasuryTotal.ToString(),
        HotPoolTotal = s.HotPoolTotal.ToString(),
        DepositAddressTotal = s.DepositAddressTotal.ToString(),
        ToppedUpTotal = s.ToppedUpTotal.ToString(),
        ObservedAt = s.ObservedAt,
    };

    public static ReconciliationHistoryDocument ToHistory(ReconciliationSnapshot s) => new()
    {
        Chain = s.Chain.ToString(),
        AssetId = s.AssetId.ToString(),
        AssetSymbol = s.AssetSymbol,
        LedgerHolding = s.LedgerHolding.ToString(),
        OnChainTotal = s.OnChainTotal.ToString(),
        Drift = s.Drift.ToString(),
        Status = s.Status.ToString(),
        AddressesScanned = s.AddressesScanned,
        AddressesUnreadable = s.AddressesUnreadable,
        ColdTreasuryTotal = s.ColdTreasuryTotal.ToString(),
        HotPoolTotal = s.HotPoolTotal.ToString(),
        DepositAddressTotal = s.DepositAddressTotal.ToString(),
        ToppedUpTotal = s.ToppedUpTotal.ToString(),
        ObservedAt = s.ObservedAt,
    };

    public static ReconciliationSnapshot FromCurrent(ReconciliationDocument d) => new(
        Enum.Parse<Chain>(d.Chain),
        Guid.Parse(d.AssetId),
        d.AssetSymbol,
        BigInteger.Parse(d.LedgerHolding),
        BigInteger.Parse(d.OnChainTotal),
        BigInteger.Parse(d.Drift),
        Enum.Parse<ReconciliationStatus>(d.Status),
        d.AddressesScanned,
        d.AddressesUnreadable,
        d.ObservedAt,
        BigInteger.Parse(d.ColdTreasuryTotal),
        BigInteger.Parse(d.HotPoolTotal),
        BigInteger.Parse(d.DepositAddressTotal),
        BigInteger.Parse(d.ToppedUpTotal));
}
