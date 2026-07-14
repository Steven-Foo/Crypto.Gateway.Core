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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

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
}
