namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

public enum MerchantStatus
{
    /// <summary>The normal operating state — set directly on <c>Create</c>, no separate review/approval step
    /// (there used to be a <c>Pending</c> state gating this; removed — nothing in the codebase ever left a
    /// merchant sitting in it, every creation path activated immediately anyway).</summary>
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
