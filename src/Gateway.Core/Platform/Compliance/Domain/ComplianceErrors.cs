using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;

/// <summary>
/// This module's business failures. Each carries a stable dotted code, which is what a UI branches on — it
/// must never pattern-match the prose, because the prose is free to change.
/// </summary>
public static class ComplianceErrors
{
    public static readonly Error InvalidScore = Error.Validation(
        "compliance.invalid_score", "A risk score threshold must be between 1 and 100.");

    public static readonly Error ReviewAboveBlock = Error.Validation(
        "compliance.review_above_block",
        "The review threshold must not exceed the block threshold, or nothing would ever reach review.");

    public static readonly Error InvalidCacheDays = Error.Validation(
        "compliance.invalid_cache_days",
        "The cache window must be between 1 and 365 days. Zero would re-screen every address on sight and "
        + "exhaust the daily quota.");

    public static readonly Error InvalidHops = Error.Validation(
        "compliance.invalid_hops",
        "The hop limit must be between 0 and 10. Zero disables the proximity rule; beyond ten hops a finding "
        + "describes the network rather than the address.");

    public static readonly Error InvalidPercent = Error.Validation(
        "compliance.invalid_percent", "A volume share must be between 0 and 100.");

    public static readonly Error UnattributedChange = Error.Validation(
        "compliance.unattributed_change",
        "A policy change must record who made it. A threshold change is a compliance act.");
}
