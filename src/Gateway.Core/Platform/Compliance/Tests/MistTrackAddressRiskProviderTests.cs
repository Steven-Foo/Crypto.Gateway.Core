using System.Net;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// Response mapping, tested against payloads copied verbatim from MistTrack's own sandbox documentation.
/// Fixtures rather than live calls: a live test would spend real daily quota and fail whenever the vendor
/// is down, which is exactly the flakiness that gets a suite ignored. The live sandbox is exercised
/// separately as a manual smoke check.
/// </summary>
public sealed class MistTrackAddressRiskProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The documented sandbox response for the Severe TRX fixture address.</summary>
    private const string SevereResponse = """
    {
      "success": true,
      "msg": "",
      "data": {
        "score": 100,
        "hacking_event": "",
        "detail_list": ["Malicious Address", "Involved Theft Activity"],
        "risk_level": "Severe",
        "risk_detail": [
          { "entity": "Theft", "volume": 0, "percent": 100,
            "risk_type": "illicit_activity", "hop_num": 1, "exposure_type": "direct" }
        ],
        "address_label": "hyperunit.xyz",
        "risk_report_url": "https://light.misttrack.io/riskReport/abc?token=xyz"
      }
    }
    """;

    private static MistTrackAddressRiskProvider Build(
        HttpStatusCode status, string body, string apiKey = "test-key")
    {
        var http = new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("https://sandbox-api.misttrack.io/")
        };

        var options = Options.Create(new MistTrackOptions
        {
            ApiKey = apiKey,
            // Pacing is disabled here so mapping assertions do not sit through the real 1/sec interval.
            RequestsPerSecond = 0
        });

        return new MistTrackAddressRiskProvider(
            http, options, NewLimiter(options), NullLogger<MistTrackAddressRiskProvider>.Instance);
    }

    /// <summary>Pacing lives in a singleton because the provider itself is transient. Tests build one per
    /// provider and disable the rate so mapping assertions do not sit through a real interval.</summary>
    private static MistTrackRateLimiter NewLimiter(IOptions<MistTrackOptions> options) =>
        new(options, new TestClock());

    [Fact]
    public async Task It_maps_a_severe_sandbox_response()
    {
        var provider = Build(HttpStatusCode.OK, SevereResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TGJ6QtCbQXo8Q5EAaJm3944B6MTpEvbwTB", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Score.ShouldBe(100);
        result.Value.RiskLevel.ShouldBe("Severe");
        result.Value.AddressLabel.ShouldBe("hyperunit.xyz");
        result.Value.ReportUrl.ShouldNotBeNull();
        result.Value.RawResponse.ShouldNotBeNull("the verbatim payload is the evidence and must be kept");
    }

    /// <summary>
    /// Indicators arrive in two shapes and a policy rule must be able to match either, so the adapter folds
    /// the human-readable detail_list and the machine risk_type codes into one list. Losing either half
    /// would make an always-block rule silently miss.
    /// </summary>
    [Fact]
    public async Task It_folds_both_the_detail_list_and_the_risk_type_codes_into_indicators()
    {
        var provider = Build(HttpStatusCode.OK, SevereResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TGJ6QtCbQXo8Q5EAaJm3944B6MTpEvbwTB", Ct);

        result.Value.Indicators.ShouldContain("Malicious Address");
        result.Value.Indicators.ShouldContain("illicit_activity");
    }

    /// <summary>
    /// MistTrack reports business failures with HTTP 200 and success:false. Treating the status code as the
    /// verdict would parse a null data block and crash, instead of surfacing a legible quota message.
    /// </summary>
    [Fact]
    public async Task A_success_false_body_on_http_200_is_a_failure_not_a_crash()
    {
        var provider = Build(HttpStatusCode.OK, """{"success": false, "msg": "ExceededDailyRateLimit"}""");

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("compliance.rate_limited");
    }

    [Fact]
    public async Task An_expired_plan_is_reported_distinctly_from_an_outage()
    {
        var provider = Build(HttpStatusCode.PaymentRequired, "");

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.Error!.Code.ShouldBe("compliance.plan_expired");
    }

    [Fact]
    public async Task A_429_is_reported_as_rate_limited()
    {
        var provider = Build(HttpStatusCode.TooManyRequests, "");

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.Error!.Code.ShouldBe("compliance.rate_limited");
    }

    [Fact]
    public async Task A_missing_api_key_fails_before_any_call_is_made()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SevereResponse);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://sandbox-api.misttrack.io/") };
        var options = Options.Create(new MistTrackOptions { ApiKey = "", RequestsPerSecond = 0 });
        var provider = new MistTrackAddressRiskProvider(
            http, options, NewLimiter(options), NullLogger<MistTrackAddressRiskProvider>.Instance);

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.Error!.Code.ShouldBe("compliance.not_configured");
        handler.Calls.ShouldBe(0, "an unconfigured provider must not spend a request finding out");
    }

    /// <summary>
    /// Every chain the gateway supports today is also covered by MistTrack, so this uses an unmapped enum
    /// value to stand in for the chain someone adds tomorrow. That is the case worth guarding: a new chain
    /// reaching an adapter that has no coin symbol for it must fail loudly, because the alternative — a
    /// default that reads as a clean score — would silently wave through every payout on that chain.
    /// </summary>
    [Fact]
    public async Task A_chain_the_adapter_does_not_map_fails_rather_than_returning_a_clean_score()
    {
        var provider = Build(HttpStatusCode.OK, SevereResponse);

        var result = await provider.GetRiskAsync((Chain)99, "unmapped-chain-address", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("compliance.chain_unsupported");
    }

    [Fact]
    public async Task The_api_key_is_sent_as_a_query_parameter()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SevereResponse);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://sandbox-api.misttrack.io/") };
        var options = Options.Create(new MistTrackOptions { ApiKey = "k-123", RequestsPerSecond = 0 });
        var provider = new MistTrackAddressRiskProvider(
            http, options, NewLimiter(options), NullLogger<MistTrackAddressRiskProvider>.Instance);

        await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        handler.LastUri.ShouldNotBeNull();
        handler.LastUri!.Query.ShouldContain("coin=TRX");
        handler.LastUri.Query.ShouldContain("api_key=k-123");
    }

    /// <summary>
    /// A REAL live response, captured verbatim on 2026-09-11 from the production API for a widely used TRX
    /// address. It is the whole reason the designation split exists: MistTrack scores this address 3 out of
    /// 100 and bands it "Low", yet it carries a sanctioned_entity entry — three hops away, through an
    /// exchange, at 2.7% of volume. Treating that as a designation would refuse an ordinary address.
    /// </summary>
    private const string LiveLowScoreIndirectSanctionsResponse = """
    {
      "success": true,
      "msg": "",
      "data": {
        "score": 3,
        "hacking_event": "",
        "detail_list": ["Involved Illicit Activity", "Interact With High-risk Tag Address"],
        "risk_level": "Low",
        "risk_detail": [
          { "entity": "htx", "risk_type": "sanctioned_entity", "volume": 7085.0, "hop_num": 3,
            "exposure_type": "indirect", "percent": 2.735 },
          { "entity": "Phishing", "risk_type": "illicit_activity", "volume": 0.611, "hop_num": 2,
            "exposure_type": "indirect", "percent": 0.0 }
        ],
        "address_label": "",
        "risk_report_url": "https://files.misttrack.io/riskReport/abc?token=xyz"
      }
    }
    """;

    /// <summary>Exposure with no exposure_type at all. Absence is not proof the exposure is remote, so it
    /// must count as direct — the safe failure is to over-refer, never to under-detect.</summary>
    private const string MissingExposureTypeResponse = """
    {
      "success": true,
      "msg": "",
      "data": {
        "score": 10,
        "risk_level": "Low",
        "detail_list": [],
        "risk_detail": [ { "entity": "OFAC", "risk_type": "sanctioned_entity", "hop_num": 0 } ],
        "address_label": "",
        "risk_report_url": ""
      }
    }
    """;

    [Fact]
    public async Task Indirect_exposure_is_reported_as_evidence_but_never_as_a_designation()
    {
        var provider = Build(HttpStatusCode.OK, LiveLowScoreIndirectSanctionsResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.IsSuccess.ShouldBeTrue();

        // Still visible to a human reviewing the address — we do not hide it.
        result.Value.Indicators.ShouldContain("sanctioned_entity");

        // But it may not force a Block, because it describes a counterparty's counterparty, not this address.
        result.Value.Designations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Direct_exposure_is_a_designation()
    {
        var provider = Build(HttpStatusCode.OK, SevereResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TGJ6QtCbQXo8Q5EAaJm3944B6MTpEvbwTB", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Designations.ShouldContain("illicit_activity");
    }

    [Fact]
    public async Task Exposure_with_no_stated_type_counts_as_direct()
    {
        var provider = Build(HttpStatusCode.OK, MissingExposureTypeResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Designations.ShouldContain("sanctioned_entity");
    }

    /// <summary>
    /// Distance and weight survive the mapping. Flat strings can only answer "is it designated"; the
    /// proximity rule needs to tell 2.7% at three hops from 60% at one, and those numbers have to come off
    /// the wire intact to do it.
    /// </summary>
    [Fact]
    public async Task Exposure_keeps_its_distance_and_its_weight()
    {
        var provider = Build(HttpStatusCode.OK, LiveLowScoreIndirectSanctionsResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e", Ct);

        var sanctions = result.Value.Exposures.Single(e => e.RiskType == "sanctioned_entity");
        sanctions.IsDirect.ShouldBeFalse();
        sanctions.Hops.ShouldBe(3);
        sanctions.Percent.ShouldBe(2.735m);
        sanctions.Entity.ShouldBe("htx");
    }

    [Fact]
    public async Task A_direct_finding_is_marked_direct_in_the_structured_exposure_too()
    {
        var provider = Build(HttpStatusCode.OK, SevereResponse);

        var result = await provider.GetRiskAsync(Chain.Tron, "TGJ6QtCbQXo8Q5EAaJm3944B6MTpEvbwTB", Ct);

        result.Value.Exposures.ShouldHaveSingleItem().IsDirect.ShouldBeTrue();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
