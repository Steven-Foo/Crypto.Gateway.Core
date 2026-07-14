using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// An outbound webhook delivery attempt record. Separate aggregate from <see cref="Merchant"/>:
/// high volume, and never loaded as part of the merchant.
///
/// <see cref="LastResponse"/> is truncated and must never carry secrets (§10).
/// </summary>
public sealed class MerchantWebhook : Entity<Guid>
{
    public const int MaxResponseLength = 1024;

    private MerchantWebhook(
        Guid id,
        Guid merchantId,
        string eventType,
        string payload,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        EventType = eventType;
        Payload = payload;
        Status = WebhookDeliveryStatus.Pending;
        RetryCount = 0;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantWebhook() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public WebhookDeliveryStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public string? LastResponse { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static MerchantWebhook Queue(Guid merchantId, string eventType, string payload, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), merchantId, eventType, payload, createdAt);

    public void MarkDelivered(string? response, DateTimeOffset deliveredAt)
    {
        Status = WebhookDeliveryStatus.Delivered;
        LastResponse = Truncate(response);
        NextRetryAt = null;
        UpdatedAt = deliveredAt;
    }

    /// <summary>Records a failed attempt. Once <paramref name="maxRetries"/> is reached the
    /// delivery is <see cref="WebhookDeliveryStatus.Exhausted"/> and will not be retried again.</summary>
    public void MarkFailed(string? response, int maxRetries, DateTimeOffset nextRetryAt, DateTimeOffset failedAt)
    {
        RetryCount++;
        LastResponse = Truncate(response);
        UpdatedAt = failedAt;

        if (RetryCount >= maxRetries)
        {
            Status = WebhookDeliveryStatus.Exhausted;
            NextRetryAt = null;
        }
        else
        {
            Status = WebhookDeliveryStatus.Failed;
            NextRetryAt = nextRetryAt;
        }
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxResponseLength ? value : value[..MaxResponseLength];
}
