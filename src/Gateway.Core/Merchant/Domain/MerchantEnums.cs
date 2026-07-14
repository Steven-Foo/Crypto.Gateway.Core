namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

public enum MerchantStatus
{
    Pending = 1,
    Active = 2,
    Suspended = 3,
    Closed = 4,
}

public enum CredentialStatus
{
    Active = 1,
    Revoked = 2,
}

public enum WebhookDeliveryStatus
{
    Pending = 1,
    Delivered = 2,
    Failed = 3,
    Exhausted = 4,
}
