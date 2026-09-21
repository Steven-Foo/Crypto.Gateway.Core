using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;

/// <summary>
/// One vendor's risk opinion about an address, normalised. Deliberately the vendor's raw output and
/// nothing more — no decision, no thresholds — so that policy lives in one place we own rather than being
/// smeared across whichever adapter is registered.
/// </summary>
/// <param name="Score">0-100, higher is worse.</param>
/// <param name="RiskLevel">The vendor's own band name, kept verbatim for the audit trail.</param>
/// <param name="Indicators">Every risk indicator and risk-type code the vendor reported, folded into one
/// list so a policy rule can match either without knowing the vendor's response shape. This is the
/// EVIDENCE list: it deliberately includes indirect exposure, because a human reviewing a held payout
/// needs to see it.</param>
/// <param name="Designations">The subset of <paramref name="Indicators"/> that describes the address
/// ITSELF rather than something it once touched — a direct designation, not exposure through a chain of
/// counterparties. Only this list may force a Block regardless of score.
///
/// <para><b>Why the two must be separate.</b> Vendors report indirect exposure using the same risk-type
/// codes as a direct hit. A widely used address several hops from an exchange that once served a
/// sanctioned customer carries a <c>sanctioned_entity</c> entry while scoring 3 out of 100. Matching the
/// always-block rule against the full list therefore refuses ordinary addresses on the strength of a
/// counterparty's counterparty. Indirect exposure is a gradient and the vendor already prices it into
/// the score, which our thresholds act on; a designation is a legal fact about this address, which no
/// threshold may override.</para></param>
/// <param name="Exposures">The same findings as <paramref name="Indicators"/>, but structured: how close
/// each one is and how much of the address's volume it accounts for.
///
/// <para>Flat strings are enough to answer "is this address designated", and nothing more. They cannot
/// distinguish 60% of volume one hop away from 0.1% five hops away, and those are very different facts
/// about an address. Keeping the structure lets policy draw a line between them without the provider's
/// response shape leaking into the policy code.</para></param>
/// <param name="RawResponse">The verbatim payload, stored as evidence.</param>
public sealed record AddressRiskReport(
    int Score,
    string RiskLevel,
    IReadOnlyList<string> Indicators,
    IReadOnlyList<string> Designations,
    IReadOnlyList<RiskExposure> Exposures,
    string? AddressLabel,
    string? ReportUrl,
    string? RawResponse);

/// <summary>
/// One risk finding, with its distance and weight.
/// </summary>
/// <param name="RiskType">The vendor's code, e.g. <c>sanctioned_entity</c>.</param>
/// <param name="IsDirect">True when the finding describes THIS address rather than a counterparty it is
/// connected to. A direct finding is a designation; an indirect one is exposure.</param>
/// <param name="Hops">How many steps away the counterparty is. Zero or one is adjacent; larger numbers are
/// progressively less about this address and more about the shape of the wider network.</param>
/// <param name="Percent">Share of the address's volume attributable to this finding, 0-100. The difference
/// between a relationship and a trace.</param>
/// <param name="Entity">The counterparty the vendor names, when it names one.</param>
public sealed record RiskExposure(
    string RiskType,
    bool IsDirect,
    int Hops,
    decimal Percent,
    string? Entity);

/// <summary>
/// The vendor seam (§8-style capability port). Implemented by <c>MistTrackAddressRiskProvider</c> today and
/// by an in-memory fake in dev and tests; a move to Crystal, TRM or Elliptic replaces this one class and
/// touches nothing else. Read-only and keyless by construction — screening never signs or moves anything.
/// </summary>
public interface IAddressRiskProvider
{
    /// <summary>The vendor's name, recorded on every evidence row.</summary>
    string Name { get; }

    /// <summary>True when this provider covers the chain at all. A chain it does not cover must be an
    /// explicit "no", never a fabricated clean score.</summary>
    bool Supports(Chain chain);

    /// <summary>
    /// Fetch the risk report. Returns a failed <see cref="Result{T}"/> for an outage, a rate-limit
    /// rejection or an unsupported chain — all expected conditions here (§7.1), none of them exceptional.
    /// </summary>
    Task<Result<AddressRiskReport>> GetRiskAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);
}
