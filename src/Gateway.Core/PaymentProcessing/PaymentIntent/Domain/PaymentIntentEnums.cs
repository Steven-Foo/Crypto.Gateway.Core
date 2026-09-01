namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>
/// The lifecycle of a deposit invoice. A <see cref="Waiting"/> intent is holding its address; it becomes
/// <see cref="Matched"/> the moment any confirmed on-chain deposit lands (exact amount or not — see
/// <c>PaymentIntent.MatchTo</c>), <see cref="Expired"/> if it times out unpaid, or <see cref="Failed"/> if
/// staff manually cancel it (e.g. a test invoice). All terminal states free the address for the merchant's
/// next invoice.
/// </summary>
public enum PaymentIntentStatus
{
    Waiting = 1,
    Matched = 2,
    Expired = 3,
    Failed = 4,
}

/// <summary>
/// What the merchant is collecting: a customer's payment, or its own top-up. Declared by the merchant when
/// the invoice is created — the two come from different portal pages, so the kind is known up front and is
/// never inferred from the chain (on-chain the two are indistinguishable).
///
/// <para>Deliberately its own enum rather than a reference to Deposit's <c>DepositKind</c>: the two modules
/// stay independently extractable (§4.5), and PaymentIntent.Domain depends on nothing but SharedKernel. The
/// value is carried across the boundary as a string on the invoice, exactly as every other cross-module fact
/// is.</para>
/// </summary>
public enum PaymentIntentKind
{
    /// <summary>A customer paying the merchant. The default for every invoice predating top-up.</summary>
    Customer = 0,

    /// <summary>The merchant funding its own balance. Priced from the merchant's separate top-up fee
    /// (default zero) and exempt from the T+N settlement hold.</summary>
    MerchantTopUp = 1,
}
