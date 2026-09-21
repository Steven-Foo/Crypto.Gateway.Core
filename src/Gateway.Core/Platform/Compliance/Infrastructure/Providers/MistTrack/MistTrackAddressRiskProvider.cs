using System.Net;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;

/// <summary>
/// Reads an address risk score from MistTrack's <c>/v3/risk_score</c>.
///
/// <para><b>Keyless and read-only by construction</b> (§10): it sends a public address and receives an
/// opinion. It cannot sign, build or broadcast anything.</para>
///
/// <para><b>The rate limiter is load-bearing, not decoration.</b> The Standard plan allows one call per
/// second; a batch of payouts screened without pacing turns straight into 429s, and a 429 is a screening
/// that produced no verdict. The gate here is in-process, which is correct precisely because the
/// screening queue is drained under the existing single-flight worker lock — only one instance calls this
/// at a time. If screening is ever moved off that single-flight path, this must become a Redis token
/// bucket or the limit will be breached by the instance count.</para>
/// </summary>
public sealed class MistTrackAddressRiskProvider : IAddressRiskProvider
{
    private readonly HttpClient _http;
    private readonly MistTrackOptions _options;
    private readonly ILogger<MistTrackAddressRiskProvider> _logger;

    // Injected, not owned: this type is registered as a typed HttpClient and is therefore TRANSIENT, so a
    // gate held as a field here would reset on every scope and pace nothing between them.
    private readonly MistTrackRateLimiter _rateLimiter;

    public MistTrackAddressRiskProvider(
        HttpClient http, IOptions<MistTrackOptions> options, MistTrackRateLimiter rateLimiter,
        ILogger<MistTrackAddressRiskProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public string Name => "MistTrack";

    /// <summary>
    /// MistTrack covers 19 chains, but this gateway only ever screens what it can actually transact on.
    /// Ethereum and Solana are listed because the adapter is ready for them, not because the gateway
    /// settles on them yet — an unsupported chain returns a failure rather than a fabricated clean score.
    /// </summary>
    public bool Supports(Chain chain) => chain is Chain.Tron or Chain.Ethereum or Chain.Solana;

    /// <summary>MistTrack identifies the chain by a coin symbol. All assets on one chain return the same
    /// result, so the native symbol is the right choice and a token symbol would gain nothing.</summary>
    private static string? CoinFor(Chain chain) => chain switch
    {
        Chain.Tron => "TRX",
        Chain.Ethereum => "ETH",
        Chain.Solana => "SOL",
        _ => null
    };

    public async Task<Result<AddressRiskReport>> GetRiskAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        if (CoinFor(chain) is not { } coin)
        {
            return Result.Failure<AddressRiskReport>(
                Error.Validation("compliance.chain_unsupported", $"MistTrack does not cover {chain}."));
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Result.Failure<AddressRiskReport>(
                Error.Validation("compliance.not_configured", "No MistTrack API key is configured."));
        }

        await _rateLimiter.WaitAsync(cancellationToken);

        var url = $"v3/risk_score?coin={Uri.EscapeDataString(coin)}"
                  + $"&address={Uri.EscapeDataString(address)}"
                  + $"&api_key={Uri.EscapeDataString(_options.ApiKey)}";

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.GetAsync(url, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MistTrack risk_score call failed for {Chain}.", chain);
            return Result.Failure<AddressRiskReport>(
                Error.Failure("compliance.provider_unreachable", "MistTrack could not be reached."));
        }

        // 402 means the plan lapsed and 429 means the quota or rate limit is spent. Both are operational
        // conditions an operator must see distinctly — "we are out of quota" and "the vendor is down" call
        // for completely different responses, and collapsing them into one message hides which is happening.
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return Result.Failure<AddressRiskReport>(
                Error.Failure("compliance.plan_expired", "The MistTrack plan is expired or exhausted."));
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Result.Failure<AddressRiskReport>(
                Error.Failure("compliance.rate_limited", "MistTrack rate limit exceeded."));
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<AddressRiskReport>(
                Error.Failure("compliance.provider_error", $"MistTrack returned HTTP {(int)response.StatusCode}."));
        }

        MistTrackEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MistTrackEnvelope>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MistTrack returned unparseable JSON.");
            return Result.Failure<AddressRiskReport>(
                Error.Failure("compliance.provider_error", "MistTrack returned an unreadable response."));
        }

        // The API reports business failures with HTTP 200 and success:false, so this check is not redundant
        // with the status handling above — dropping it would turn "ExceededDailyRateLimit" into a parse of
        // a null data block, i.e. a crash instead of a legible operational message.
        if (envelope is null || !envelope.Success || envelope.Data is null)
        {
            var msg = envelope?.Msg ?? "unknown error";
            var code = msg.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
                ? "compliance.rate_limited"
                : "compliance.provider_error";
            return Result.Failure<AddressRiskReport>(Error.Failure(code, $"MistTrack: {msg}"));
        }

        var data = envelope.Data;

        // Indicators come from two places in the response and both matter: detail_list carries the
        // human-readable flags ("Sanctioned Entity"), while risk_detail carries the machine risk_type
        // codes ("sanctioned_entity"). A policy rule must be able to match either, so they are folded into
        // one list here rather than forcing every rule to know the vendor's response shape.
        var indicators = new List<string>();
        if (data.DetailList is not null)
        {
            indicators.AddRange(data.DetailList.Where(d => !string.IsNullOrWhiteSpace(d)));
        }

        if (data.RiskDetail is not null)
        {
            indicators.AddRange(data.RiskDetail
                .Select(d => d.RiskType)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!));
        }

        if (!string.IsNullOrWhiteSpace(data.HackingEvent))
        {
            indicators.Add(data.HackingEvent);
        }

        // Designations: the subset that describes THIS address rather than a counterparty it is connected
        // to. MistTrack tags every risk_detail entry with exposure_type and a hop count, and reuses the
        // same risk_type codes for both — so "sanctioned_entity" appears on an address three hops from an
        // exchange exactly as it would on a sanctioned address itself. Verified live: a TRX address
        // MistTrack scores 3 ("Low") carries a sanctioned_entity entry at exposure_type "indirect",
        // hop_num 3, 2.7% of volume. Feeding that to the always-block rule refuses ordinary addresses.
        //
        // Indirect exposure is NOT discarded — it stays in Indicators as evidence and the vendor has
        // already priced it into the score our thresholds act on. It simply may not force a Block.
        //
        // A MISSING exposure_type counts as direct: absence of the field is not proof the exposure is
        // remote, and the safe failure here is to over-refer rather than to under-detect.
        var designations = new List<string>();
        if (data.RiskDetail is not null)
        {
            designations.AddRange(data.RiskDetail
                .Where(d => !string.IsNullOrWhiteSpace(d.RiskType) && IsDirect(d.ExposureType))
                .Select(d => d.RiskType!));
        }

        // A named hacking event is an assertion about this address, not about a counterparty.
        if (!string.IsNullOrWhiteSpace(data.HackingEvent))
        {
            designations.Add(data.HackingEvent);
        }

        // The same findings, kept structured. The flat lists above answer "is it designated"; these keep
        // the distance and the weight, so policy can tell a close, heavy exposure from a distant trace
        // without needing to know the vendor's response shape.
        var exposures = new List<RiskExposure>();
        if (data.RiskDetail is not null)
        {
            exposures.AddRange(data.RiskDetail
                .Where(d => !string.IsNullOrWhiteSpace(d.RiskType))
                .Select(d => new RiskExposure(
                    d.RiskType!,
                    IsDirect(d.ExposureType),
                    d.HopNum,
                    // The vendor sends percent as a JSON number; money never touches this, it is a
                    // proportion used to compare against a threshold (§14 applies to amounts, not ratios).
                    (decimal)d.Percent,
                    string.IsNullOrWhiteSpace(d.Entity) ? null : d.Entity)));
        }

        return Result.Success<AddressRiskReport>(new AddressRiskReport(
            data.Score,
            data.RiskLevel ?? "Unknown",
            indicators.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            designations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            exposures,
            string.IsNullOrWhiteSpace(data.AddressLabel) ? null : data.AddressLabel,
            data.RiskReportUrl,
            body));
    }

    /// <summary>MistTrack marks each risk_detail entry "direct" or "indirect". Anything not explicitly
    /// indirect is treated as direct, so an unrecognised or absent value over-refers rather than
    /// under-detects.</summary>
    private static bool IsDirect(string? exposureType) =>
        !string.Equals(exposureType?.Trim(), "indirect", StringComparison.OrdinalIgnoreCase);
}
