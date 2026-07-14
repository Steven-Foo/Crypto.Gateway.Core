using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a withdrawal is rejected (approval denied) or fails before broadcast (no funds left
/// the platform). The Ledger consumes it to <b>release</b> the reserved funds back to the merchant.
/// Never raised after broadcast — once funds may be on-chain, a stuck withdrawal is an ops incident,
/// not an automatic release.
/// </summary>
public sealed record WithdrawalFailed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid WithdrawalId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string Reason,
    DateTimeOffset FailedAt) : IDomainEvent, IIntegrationEvent;
