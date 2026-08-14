using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a withdrawal has confirmed on-chain. The Ledger consumes it to <b>settle</b> — move
/// the amount out of custody and book the fee as revenue. Amounts are exact base-unit integer strings
/// (§14). <see cref="MerchantTransactionId"/>/<see cref="DestinationAddress"/>/<see cref="CallbackUrl"/> exist so
/// Notification's withdrawal callback handler can build the merchant payload without looking anything up
/// (§4.5, mirrors <c>PaymentIntentMatched</c>). The publisher (Withdrawal) owns this contract; consumers
/// reference this Events project.
/// </summary>
/// <remarks>
/// <see cref="GasAssetId"/>/<see cref="GasFeeBaseUnits"/> carry the native-coin fee the platform paid on-chain
/// so the Ledger can book it as a platform gas expense (5c) — the Ledger stays chain-agnostic (§4.6), receiving
/// the fee as data. <see cref="GasAssetId"/> is null (and the fee zero) when no gas asset is configured or the
/// engine charged no fee, in which case no gas journal is written.
/// </remarks>
public sealed record WithdrawalConfirmed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid WithdrawalId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string TransactionHash,
    DateTimeOffset ConfirmedAt,
    string MerchantTransactionId,
    string DestinationAddress,
    string? CallbackUrl,
    string? GasAssetId = null,
    string GasFeeBaseUnits = "0") : IDomainEvent, IIntegrationEvent;
