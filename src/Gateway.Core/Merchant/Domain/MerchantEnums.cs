namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

public enum MerchantStatus
{
    Pending = 1,
    Active = 2,

    /// <summary>An admin risk-hold. A frozen merchant cannot transact (deposit-address requests, user
    /// payouts, and earnings cash-out are all blocked via <c>CanTransact</c>) until an admin re-activates
    /// it. Reversible — <c>Activate</c> unfreezes. On-chain deposits already sent to an issued address are
    /// still credited to the ledger (funds are never lost); freeze stops issuing/settling, not recording.</summary>
    Frozen = 3,

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
