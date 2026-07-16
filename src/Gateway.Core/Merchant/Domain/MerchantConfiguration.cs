using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// Per-merchant, asset-independent settings. Anything expressed as an <em>amount</em> is per-asset
/// and belongs on <see cref="MerchantAssetPolicy"/> instead — a single scalar threshold cannot
/// mean the same thing for TRX and USDT.
/// </summary>
public sealed class MerchantConfiguration : Entity<Guid>
{
    public const int MaxWebhookRetryCount = 20;
    public const int DefaultWebhookRetryCount = 5;

    private MerchantConfiguration(Guid id, Guid merchantId, DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        AutoSweepEnabled = false;
        WebhookRetryCount = DefaultWebhookRetryCount;
        IsEnabled = true;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantConfiguration() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public bool AutoSweepEnabled { get; private set; }
    public int WebhookRetryCount { get; private set; }
    public bool IsEnabled { get; private set; }

    /// <summary>Comma-separated IP allowlist for this merchant's inbound API calls (BO-managed, synced to
    /// Cloudflare). Null/empty means no IP restriction is enforced. Stored as CSV like APIGateway's
    /// <c>Merchant.AllowedIps</c> — small, bounded list, not worth a child table.</summary>
    public string? AllowedIpsCsv { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> AllowedIps =>
        string.IsNullOrWhiteSpace(AllowedIpsCsv)
            ? []
            : AllowedIpsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static MerchantConfiguration CreateDefault(Guid merchantId, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), merchantId, createdAt);

    internal Result Update(bool autoSweepEnabled, int webhookRetryCount, bool isEnabled, DateTimeOffset updatedAt)
    {
        if (webhookRetryCount is < 0 or > MaxWebhookRetryCount)
            return Result.Failure(MerchantErrors.WebhookRetryCountInvalid);

        AutoSweepEnabled = autoSweepEnabled;
        WebhookRetryCount = webhookRetryCount;
        IsEnabled = isEnabled;
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    /// <summary>
    /// Replaces the IP allowlist and reports what actually changed, so the caller can push only the delta
    /// to Cloudflare (matching APIGateway's <c>BoMerchantController.UpdateAllowedIps</c> exactly: IP format
    /// validation happens before this call, at the edge — this method only diffs and stores).
    /// </summary>
    internal AllowedIpsChange UpdateAllowedIps(IReadOnlyCollection<string> validIps, DateTimeOffset updatedAt)
    {
        var oldSet = new HashSet<string>(AllowedIps, StringComparer.OrdinalIgnoreCase);
        var newSet = new HashSet<string>(validIps, StringComparer.OrdinalIgnoreCase);

        var added = newSet.Except(oldSet, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = oldSet.Except(newSet, StringComparer.OrdinalIgnoreCase).ToList();

        AllowedIpsCsv = newSet.Count == 0 ? null : string.Join(',', newSet);
        UpdatedAt = updatedAt;

        return new AllowedIpsChange(added, removed, [.. newSet]);
    }
}

/// <summary>What changed after an allowlist update — <see cref="Added"/>/<see cref="Removed"/> are exactly
/// what needs to be pushed to Cloudflare; <see cref="Current"/> is the full resulting list.</summary>
public sealed record AllowedIpsChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Current);
