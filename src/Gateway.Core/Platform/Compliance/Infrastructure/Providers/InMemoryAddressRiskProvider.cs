using System.Collections.Concurrent;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers;

/// <summary>
/// The dev and test stand-in for a real vendor — the same DI seam the in-memory chain source occupies (§8).
/// It contacts nothing and spends no quota, so the dev round-trip stays offline and free.
///
/// <para><b>Everything is clean unless staged.</b> An unknown address scores 0. That is the right default
/// for a fake, because a dev environment that randomly blocked payouts would train people to ignore the
/// control — but it does mean this must never be registered in Production, where a fabricated clean score
/// would be indistinguishable from a real one. Composition enforces that, exactly as it does for the
/// signer (§10).</para>
/// </summary>
public sealed class InMemoryAddressRiskProvider : IAddressRiskProvider
{
    private readonly ConcurrentDictionary<string, AddressRiskReport> _staged = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "InMemory";

    public bool Supports(Chain chain) => true;

    /// <summary>Stage a risk report for an address so a test or a demo can exercise Review and Block.
    /// Staged indicators count as DIRECT designations, because staging "sanctioned_entity" here means
    /// "this address is designated" — the fake has no counterparty graph to be indirect about.</summary>
    public void Stage(Chain chain, string address, int score, string riskLevel, params string[] indicators) =>
        _staged[Key(chain, address)] = new AddressRiskReport(
            score, riskLevel, indicators, indicators, Exposures: [], AddressLabel: null, ReportUrl: null,
            RawResponse: null);

    /// <summary>Stage a report whose risk indicators are INDIRECT exposure — present as evidence but not a
    /// designation, so they must never force a Block. Exists so a test can prove that distinction.</summary>
    public void StageIndirect(Chain chain, string address, int score, string riskLevel, params string[] indicators) =>
        StageIndirect(chain, address, score, riskLevel, hops: 3, percent: 2.5m, indicators);

    /// <summary>Stage indirect exposure at a chosen distance and weight, so the proximity rule's thresholds
    /// can be exercised against specific numbers rather than one fixed pair.</summary>
    public void StageIndirect(
        Chain chain, string address, int score, string riskLevel, int hops, decimal percent,
        params string[] indicators) =>
        _staged[Key(chain, address)] = new AddressRiskReport(
            score, riskLevel, indicators, [],
            [.. indicators.Select(i => new RiskExposure(i, IsDirect: false, hops, percent, Entity: null))],
            AddressLabel: null, ReportUrl: null, RawResponse: null);

    public Task<Result<AddressRiskReport>> GetRiskAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var report = _staged.TryGetValue(Key(chain, address), out var staged)
            ? staged
            : new AddressRiskReport(0, "Low", [], [], [], AddressLabel: null, ReportUrl: null, RawResponse: null);

        return Task.FromResult(Result.Success<AddressRiskReport>(report));
    }

    private static string Key(Chain chain, string address) => $"{chain}:{address}";
}
