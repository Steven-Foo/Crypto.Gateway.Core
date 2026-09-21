using System.Text.Json.Serialization;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers.MistTrack;

/// <summary>
/// MistTrack's envelope. Note it reports business failures with HTTP 200 and <c>success:false</c>, so the
/// status code alone is never sufficient to decide whether a screening worked.
/// </summary>
internal sealed class MistTrackEnvelope
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("data")] public MistTrackRiskData? Data { get; set; }
    [JsonPropertyName("retry_after")] public int? RetryAfter { get; set; }
}

internal sealed class MistTrackRiskData
{
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("risk_level")] public string? RiskLevel { get; set; }
    [JsonPropertyName("detail_list")] public List<string>? DetailList { get; set; }
    [JsonPropertyName("risk_detail")] public List<MistTrackRiskDetail>? RiskDetail { get; set; }
    [JsonPropertyName("hacking_event")] public string? HackingEvent { get; set; }
    [JsonPropertyName("address_label")] public string? AddressLabel { get; set; }
    [JsonPropertyName("risk_report_url")] public string? RiskReportUrl { get; set; }
}

internal sealed class MistTrackRiskDetail
{
    [JsonPropertyName("entity")] public string? Entity { get; set; }
    [JsonPropertyName("risk_type")] public string? RiskType { get; set; }
    [JsonPropertyName("exposure_type")] public string? ExposureType { get; set; }
    [JsonPropertyName("hop_num")] public int HopNum { get; set; }
    [JsonPropertyName("volume")] public double Volume { get; set; }
    [JsonPropertyName("percent")] public double Percent { get; set; }
}
